using System.Text;
using System.Text.Json.Serialization;
using ECUStudio.Binary;
using ECUStudio.Calibration.Definitions;
using ECUStudio.Calibration.Library;
using ECUStudio.Calibration.Plugins;
using ECUStudio.Core;

namespace ECUStudio.Application.Library;

[JsonConverter(typeof(JsonStringEnumConverter<DefinitionOrigin>))]
public enum DefinitionOrigin
{
    /// <summary>Uploaded into the project by the user (content stored with the project).</summary>
    Upload,
    /// <summary>Bound by the user from the local library (reference only; read from the archive).</summary>
    Library,
    /// <summary>Picked automatically from the library for this binary (Exact/Strong match only).</summary>
    AutoLibrary,
}

/// <summary>A definition bound to a project.</summary>
public sealed record ProjectDefinition
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required LibraryFormat Format { get; init; }
    public required DefinitionOrigin Origin { get; init; }
    public string? Sha256 { get; init; }
    public long Size { get; init; }
    public string? LibraryEntryId { get; init; }
    public int MapCount { get; init; }
    public LibraryIdentifiers Identifiers { get; init; } = new();
    public CompatibilityReport? Compatibility { get; init; }
    /// <summary>Binary the compatibility was checked against.</summary>
    public string? CheckedAgainst { get; init; }
    public DateTimeOffset BoundAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>What the analysis used as its external definition, and why. Stored in the report.</summary>
public sealed record DefinitionBinding
{
    public required DefinitionOrigin Origin { get; init; }
    public required string Name { get; init; }
    public required LibraryFormat Format { get; init; }
    public string? LibraryEntryId { get; init; }
    public MatchLevel? Level { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public CompatibilityReport? Compatibility { get; init; }
    /// <summary>False when the definition was found but not applied (incompatible, unreadable, not importable).</summary>
    public bool Applied { get; init; }
}

/// <summary>Result of reading a definition before binding it.</summary>
public sealed record DefinitionPreview
{
    public required string Name { get; init; }
    public required LibraryFormat Format { get; init; }
    public required bool Importable { get; init; }
    public int MapCount { get; init; }
    public LibraryIdentifiers Identifiers { get; init; } = new();
    public IReadOnlyList<string> Notes { get; init; } = [];
    public CompatibilityReport? Compatibility { get; init; }
    public string? Binary { get; init; }
    public string? BinarySoftware { get; init; }
    public string? BinaryHardware { get; init; }
    public string? EcuFamily { get; init; }
    public string? Error { get; init; }
}

public sealed record ResolvedDefinition(ExternalDefinition? Definition, DefinitionBinding? Binding, IReadOnlyList<string> Notes);

/// <summary>Reads, checks and selects external definitions for a binary (project binding first, then the library).</summary>
public sealed class DefinitionService(DefinitionLibrary library, PluginRegistry plugins)
{
    public DefinitionLibrary Library => library;

    public static string DecodeText(byte[] content)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(content); }
    }

    public BinaryKey KeyOf(BinaryImage image)
    {
        var (plugin, _) = plugins.Detect(image);
        var ident = plugin.Identify(image);
        var engine = Vehicle.Resolution.EcuIdentificationProvider.ParseEngineText(ident.EngineCode.Text) is { } e ? e.Litres : null;
        return new BinaryKey(Text(ident.SoftwareNumber), Text(ident.HardwareNumber), Text(ident.OemPartNumber), ident.EcuFamily, image.Sha256, engine, Text(ident.ProjectCode));
    }

    public DefinitionPreview Preview(string fileName, byte[] content, BinaryImage? target)
    {
        var format = LibraryScanner.FormatOf(fileName);
        var text = DecodeText(content);
        var ids = LibraryScanner.Merge(LibraryScanner.Identify(fileName), LibraryScanner.Identify(text.Length > LibraryScanner.MaxHeaderBytes ? text[..LibraryScanner.MaxHeaderBytes] : text));
        var preview = new DefinitionPreview { Name = fileName, Format = format, Importable = IsImportable(format), Identifiers = ids };
        EcuIdentification? ident = null;
        if (target is not null)
        {
            var (plugin, _) = plugins.Detect(target);
            ident = plugin.Identify(target);
            preview = preview with { Binary = target.FileName, BinarySoftware = Text(ident.SoftwareNumber), BinaryHardware = Text(ident.HardwareNumber), EcuFamily = ident.EcuFamily };
        }
        if (!preview.Importable) return preview with { Error = NotImportableReason(format) };
        ExternalDefinition def;
        try { def = DefinitionImporters.Import(fileName, text); }
        catch (DefinitionException ex) { return preview with { Error = ex.Message }; }
        return preview with
        {
            MapCount = def.Maps.Count, Notes = def.Notes,
            Compatibility = target is null || ident is null ? null : DefinitionCompatibility.Check(def, target, ident, ids),
        };
    }

    /// <summary>
    /// Definition for one analysis: the project binding when there is one, otherwise the best Exact/Strong importable
    /// library match. A definition that fails the compatibility check is reported but not applied.
    /// </summary>
    public ResolvedDefinition Resolve(ProjectDefinition? bound, Func<Guid, byte[]?> projectContent, BinaryImage image)
    {
        var (plugin, _) = plugins.Detect(image);
        var ident = plugin.Identify(image);
        if (bound is not null) return FromBinding(bound, projectContent, image, ident);
        return FromLibrary(image, ident);
    }

    private ResolvedDefinition FromBinding(ProjectDefinition bound, Func<Guid, byte[]?> projectContent, BinaryImage image, EcuIdentification ident)
    {
        byte[]? content;
        try
        {
            content = bound.Origin == DefinitionOrigin.Upload ? projectContent(bound.Id)
                : bound.LibraryEntryId is { } id ? library.Read(id).Content : null;
        }
        catch (EcuStudioException ex)
        {
            return new(null, Binding(bound, applied: false, compat: null, reasons: [ex.Message]), [$"Bound definition {bound.Name} could not be read: {ex.Message}"]);
        }
        if (content is null) return new(null, Binding(bound, false, null, ["definition content is missing"]), [$"Bound definition {bound.Name} is missing"]);
        try
        {
            var def = DefinitionImporters.Import(bound.Name, DecodeText(content));
            var compat = DefinitionCompatibility.Check(def, image, ident, bound.Identifiers);
            // A definition the user bound explicitly is applied even with warnings; the report shows them.
            var notes = compat.Status == CompatibilityStatus.Compatible ? [] : compat.Reasons.Select(r => $"Bound definition {bound.Name}: {r}").ToList();
            return new(def, Binding(bound, true, compat, ["bound to the project by the user"]), notes);
        }
        catch (DefinitionException ex)
        {
            return new(null, Binding(bound, false, null, [ex.Message]), [$"Bound definition {bound.Name} could not be imported: {ex.Message}"]);
        }
    }

    private ResolvedDefinition FromLibrary(BinaryImage image, EcuIdentification ident)
    {
        var key = new BinaryKey(Text(ident.SoftwareNumber), Text(ident.HardwareNumber), Text(ident.OemPartNumber), ident.EcuFamily, image.Sha256, null, Text(ident.ProjectCode));
        var matches = library.Match(key, 20).Where(m => m.IsDefinition && m.Level <= MatchLevel.Strong).ToList();
        if (matches.Count == 0) return new(null, null, []);
        var notes = new List<string>();
        foreach (var m in matches)
        {
            if (!m.Importable) { notes.Add($"Library has {m.Entry.Format} {m.Entry.RelativePath} ({m.Level}) but this format cannot be imported: export it as A2L"); continue; }
            if (!m.Entry.Available) { notes.Add($"Library entry {m.Entry.RelativePath} ({m.Level}) is not downloaded yet"); continue; }
            try
            {
                var (_, content) = library.Read(m.Entry.Id);
                var def = DefinitionImporters.Import(Path.GetFileName(m.Entry.RelativePath), DecodeText(content));
                var compat = DefinitionCompatibility.Check(def, image, ident, m.Entry.Identifiers);
                var binding = new DefinitionBinding
                {
                    Origin = DefinitionOrigin.AutoLibrary, Name = m.Entry.RelativePath, Format = m.Entry.Format, LibraryEntryId = m.Entry.Id,
                    Level = m.Level, Reasons = m.Reasons, Compatibility = compat, Applied = compat.Status != CompatibilityStatus.Incompatible,
                };
                if (!binding.Applied) { notes.Add($"Library definition {m.Entry.RelativePath} matched ({m.Level}) but failed the compatibility check: {string.Join("; ", compat.Reasons)}"); continue; }
                notes.Add($"Definition {m.Entry.RelativePath} picked from the library ({m.Level}: {string.Join(", ", m.Reasons)})");
                return new(def with { Name = Path.GetFileName(m.Entry.RelativePath) }, binding, notes);
            }
            catch (Exception ex) when (ex is EcuStudioException or IOException or UnauthorizedAccessException)
            {
                notes.Add($"Library definition {m.Entry.RelativePath} could not be used: {ex.Message}");
            }
        }
        var best = matches[0];
        return new(null, new DefinitionBinding
        {
            Origin = DefinitionOrigin.AutoLibrary, Name = best.Entry.RelativePath, Format = best.Entry.Format, LibraryEntryId = best.Entry.Id,
            Level = best.Level, Reasons = best.Reasons, Applied = false,
        }, notes);
    }

    private static DefinitionBinding Binding(ProjectDefinition d, bool applied, CompatibilityReport? compat, IReadOnlyList<string> reasons) => new()
    {
        Origin = d.Origin, Name = d.Name, Format = d.Format, LibraryEntryId = d.LibraryEntryId, Reasons = reasons, Compatibility = compat ?? d.Compatibility, Applied = applied,
    };

    public static bool IsImportable(LibraryFormat f) => f is LibraryFormat.A2L or LibraryFormat.Xdf or LibraryFormat.EcuDef;

    public static string NotImportableReason(LibraryFormat f) => f switch
    {
        LibraryFormat.Damos => "DAMOS files are indexed and matched but not imported: export the project as ASAP2 (*.a2l) and import that.",
        LibraryFormat.Ols => "OLS is a closed WinOLS format: only its name is indexed. Export the map pack as XDF or A2L.",
        LibraryFormat.Kp => "KP map packs are indexed but not imported: export as XDF or A2L.",
        _ => $"{f} files cannot be used as a definition.",
    };

    private static string? Text(Param p) => p.IsKnown ? p.Text : null;
}

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
    /// <summary>Found, fetched from a torrent source if needed, verified and bound automatically.</summary>
    Acquired,
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
    /// <summary>File inside an archive (zip) the definition was read from.</summary>
    public string? ArchivePath { get; init; }
    /// <summary>Full verification against the binary (score, evidence, conflicts, relocated maps).</summary>
    public DefinitionCompatibilityResult? Verification { get; init; }
    public MatchConfidence? MatchConfidence { get; init; }
    public string? TorrentHash { get; init; }
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
    public DefinitionCompatibilityResult? Verification { get; init; }
    public string? ArchivePath { get; init; }
}

/// <summary>A definition file read from the library, unpacked from an archive when needed.</summary>
public sealed record DefinitionSource(string Name, LibraryFormat Format, byte[] Content, string? ArchivePath);

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

    // Parsed definitions by content hash: a large A2L is parsed once per process, not per analysis.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExternalDefinition> _parsed = new();

    /// <summary>Imports (or reuses the parsed) definition for this content.</summary>
    public ExternalDefinition Import(string name, byte[] content)
    {
        var key = Hashing.Sha256Hex(content) + "|" + name;
        if (_parsed.TryGetValue(key, out var cached)) return cached;
        var def = DefinitionImporters.Import(name, DecodeText(content));
        if (_parsed.Count > 8) _parsed.Clear();
        _parsed[key] = def;
        return def;
    }

    /// <summary>Reads a library entry; for a zip, the importable definition inside it (A2L preferred).</summary>
    public DefinitionSource ReadDefinition(string entryId, string? archivePath = null)
    {
        var (entry, content) = library.Read(entryId);
        var name = Path.GetFileName(entry.RelativePath);
        if (entry.Format != LibraryFormat.Archive) return new DefinitionSource(name, entry.Format, content, null);
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new DefinitionException($"{name}: only zip archives can be opened");
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(content), System.IO.Compression.ZipArchiveMode.Read);
        var candidates = zip.Entries.Where(e => e.Length > 0 && IsImportable(LibraryScanner.FormatOf(e.FullName))).ToList();
        var chosen = archivePath is not null ? zip.GetEntry(archivePath)
            : candidates.OrderBy(e => LibraryScanner.FormatOf(e.FullName) switch { LibraryFormat.A2L => 0, LibraryFormat.EcuDef => 1, _ => 2 }).ThenByDescending(e => e.Length).FirstOrDefault();
        if (chosen is null)
        {
            var inside = string.Join(", ", zip.Entries.Select(e => Path.GetExtension(e.Name)).Where(x => x.Length > 0).Distinct().Take(8));
            throw new DefinitionException($"{name} contains no importable definition (A2L/XDF){(inside.Length > 0 ? $"; it has {inside}" : "")}");
        }
        if (chosen.Length > DefinitionLibrary.MaxImportBytes) throw new EcuStudioException("LIBRARY_TOO_LARGE", $"{chosen.FullName} is too large");
        using var s = chosen.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new DefinitionSource(chosen.Name, LibraryScanner.FormatOf(chosen.FullName), ms.ToArray(), chosen.FullName);
    }

    /// <summary>Imports and verifies a definition against a binary; only the maps that fit are kept.</summary>
    public VerifiedDefinition Verify(ExternalDefinition def, BinaryImage image, LibraryIdentifiers ids)
    {
        var (plugin, _) = plugins.Detect(image);
        return DefinitionVerifier.Verify(def, image, plugin.Identify(image), ids);
    }

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
        return new BinaryKey(Text(ident.SoftwareNumber), Text(ident.HardwareNumber), Text(ident.OemPartNumber), ident.EcuFamily, image.Sha256, engine, Text(ident.ProjectCode), Text(ident.SoftwareVersion));
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
            Compatibility = target is null || ident is null ? null : DefinitionVerifier.Verify(def, target, ident, ids).Result.ToReport(),
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
        DefinitionSource? source;
        try
        {
            source = bound.Origin == DefinitionOrigin.Upload
                ? projectContent(bound.Id) is { } c ? new DefinitionSource(bound.Name, bound.Format, c, null) : null
                : bound.LibraryEntryId is { } id ? ReadDefinition(id, bound.ArchivePath) : null;
        }
        catch (Exception ex) when (ex is EcuStudioException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(null, Binding(bound, applied: false, compat: null, reasons: [ex.Message]), [$"Bound definition {bound.Name} could not be read: {ex.Message}"]);
        }
        if (source is null) return new(null, Binding(bound, false, null, ["definition content is missing"]), [$"Bound definition {bound.Name} is missing"]);
        try
        {
            var def = Import(source.Name, source.Content);
            var verified = DefinitionVerifier.Verify(def, image, ident, bound.Identifiers);
            var result = verified.Result;
            // Only the maps that fit this binary are used; the report lists the rest.
            var notes = result.Status is DefinitionFit.Exact or DefinitionFit.Compatible && result.InvalidMaps == 0 ? new List<string>()
                : [.. result.Conflicts.Select(r => $"Definition {bound.Name}: {r}")];
            if (result.RelocatedMaps > 0) notes.Add($"Definition {bound.Name}: {result.RelocatedMaps} map(s) relocated for this software version");
            var reasons = bound.Origin == DefinitionOrigin.Acquired ? ["found and verified automatically"] : new[] { "bound to the project by the user" };
            return new(verified.Definition, Binding(bound, verified.Definition.Maps.Count > 0, result.ToReport(), reasons) with { Verification = result }, notes);
        }
        catch (DefinitionException ex)
        {
            return new(null, Binding(bound, false, null, [ex.Message]), [$"Bound definition {bound.Name} could not be imported: {ex.Message}"]);
        }
    }

    private ResolvedDefinition FromLibrary(BinaryImage image, EcuIdentification ident)
    {
        var key = new BinaryKey(Text(ident.SoftwareNumber), Text(ident.HardwareNumber), Text(ident.OemPartNumber), ident.EcuFamily, image.Sha256, null, Text(ident.ProjectCode), Text(ident.SoftwareVersion));
        var matches = library.Match(key, 20).Where(m => m.IsDefinition && m.Level <= MatchLevel.Strong).ToList();
        if (matches.Count == 0) return new(null, null, []);
        var notes = new List<string>();
        foreach (var m in matches)
        {
            if (!m.Importable) { notes.Add($"Library has {m.Entry.Format} {m.Entry.RelativePath} ({m.Level}) but this format cannot be imported: export it as A2L"); continue; }
            if (!m.Entry.Available) { notes.Add($"Library entry {m.Entry.RelativePath} ({m.Level}) is not downloaded yet"); continue; }
            try
            {
                var source = ReadDefinition(m.Entry.Id);
                var def = Import(source.Name, source.Content);
                var verified = DefinitionVerifier.Verify(def, image, ident, m.Entry.Identifiers);
                var binding = new DefinitionBinding
                {
                    Origin = DefinitionOrigin.AutoLibrary, Name = m.Entry.RelativePath, Format = m.Entry.Format, LibraryEntryId = m.Entry.Id,
                    Level = m.Level, Reasons = m.Reasons, Compatibility = verified.Result.ToReport(), Verification = verified.Result,
                    Applied = verified.Result.Status is DefinitionFit.Exact or DefinitionFit.Compatible,
                };
                if (!binding.Applied) { notes.Add($"Library definition {m.Entry.RelativePath} matched ({m.Level}) but did not pass verification ({verified.Result.Status}, {verified.Result.Score} %)"); continue; }
                notes.Add($"Definition {m.Entry.RelativePath} picked from the library ({m.Level}: {string.Join(", ", m.Reasons)})");
                return new(verified.Definition with { Name = source.Name }, binding, notes);
            }
            catch (Exception ex) when (ex is EcuStudioException or IOException or UnauthorizedAccessException or InvalidDataException)
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
        Verification = d.Verification, ArchivePath = d.ArchivePath,
    };

    public static bool IsImportable(LibraryFormat f) => f is LibraryFormat.A2L or LibraryFormat.Xdf or LibraryFormat.EcuDef;

    /// <summary>Formats an acquisition may fetch: importable definitions and zip archives that may hold one.</summary>
    public static bool IsAcquirable(LibraryEntry e) => IsImportable(e.Format) || e.Format == LibraryFormat.Archive && e.RelativePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    public static string NotImportableReason(LibraryFormat f) => f switch
    {
        LibraryFormat.Damos => "DAMOS files are indexed and matched but not imported: export the project as ASAP2 (*.a2l) and import that.",
        LibraryFormat.Ols => "OLS is a closed WinOLS format: only its name is indexed. Export the map pack as XDF or A2L.",
        LibraryFormat.Kp => "KP map packs are indexed but not imported: export as XDF or A2L.",
        _ => $"{f} files cannot be used as a definition.",
    };

    private static string? Text(Param p) => p.IsKnown ? p.Text : null;
}

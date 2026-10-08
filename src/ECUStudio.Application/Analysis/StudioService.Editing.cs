using System.Text.Json.Serialization;
using ECUStudio.Application.Projects;
using ECUStudio.Binary;
using ECUStudio.Binary.Editing;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;

namespace ECUStudio.Application.Analysis;

/// <summary>Patch history of one project file. The stored file itself is never modified.</summary>
public sealed record FileEdits(Guid FileId, IReadOnlyList<EditOperation> History, IReadOnlyList<EditOperation> Redo);

public sealed record EditSummary(Guid Id, DateTimeOffset At, string Source, string? MapId, string Description, int ChangedBytes, int FirstAddress);

public sealed record EditState
{
    public required Guid FileId { get; init; }
    public required int Size { get; init; }
    public required string OriginalSha256 { get; init; }
    public required string WorkingSha256 { get; init; }
    public required int ChangedBytes { get; init; }
    public required IReadOnlyList<ByteRange> ChangedRanges { get; init; }
    public required IReadOnlyList<EditSummary> History { get; init; }
    public required int RedoCount { get; init; }
    public bool CanUndo => History.Count > 0;
    public bool CanRedo => RedoCount > 0;
    /// <summary>Map whose values were touched by each changed range (null = outside known maps).</summary>
    public IReadOnlyList<ChangedRegion> Regions { get; init; } = [];
}

public sealed record ChangedRegion(int Start, int Length, string? MapId, string? MapName);

public sealed record WorkingHexPage(int Offset, int Length, int FileSize, byte[] Working, byte[] Original, IReadOnlyList<ByteRange> Changed);

[JsonConverter(typeof(JsonStringEnumConverter<SaveCheckStatus>))]
public enum SaveCheckStatus { Pass, Warn, Fail }

public sealed record SaveCheckItem(string Id, SaveCheckStatus Status, string Message);

public sealed record SaveCheck
{
    public required bool CanSave { get; init; }
    public required IReadOnlyList<SaveCheckItem> Checks { get; init; }
    public required ChecksumReport Checksum { get; init; }
    public required string SuggestedName { get; init; }
    /// <summary>Acknowledgements still needed before saving ("checksum", "code").</summary>
    public IReadOnlyList<string> NeedsAcknowledgement { get; init; } = [];
}

public sealed record SaveRequest(string? Name, bool AcknowledgeChecksumRisk = false, bool AcknowledgeCodeChanges = false);

public sealed record SaveResult(ProjectFile File, SaveCheck Check, bool Verified, IReadOnlyList<string> Verification);

public sealed record HexEdit(int Address, string Hex, string? Description = null);

public sealed record MapEditRequest(string MapId, MapOperation Operation, string? Description = null);

public sealed record RevertRequest(IReadOnlyList<ByteRange>? Ranges = null, string? MapId = null, bool All = false);

/// <summary>
/// Binary editing over the patch model. Every operation is loaded from and saved to the project as a patch history
/// against the immutable stored file; saving always produces a new project file after the safety checks.
/// </summary>
public sealed partial class StudioService
{
    private async Task<(Project Project, ProjectFile File, BinaryDocument Doc)> LoadDocumentAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        var project = await GetProjectAsync(projectId, ct);
        var file = project.Files.FirstOrDefault(f => f.Id == fileId) ?? throw new NotFoundException($"File {fileId} not found in project");
        var bytes = await store.GetFileContentAsync(file.Id, ct) ?? throw new NotFoundException($"Content for file {file.Name} missing");
        var edits = project.Edits.FirstOrDefault(e => e.FileId == fileId);
        return (project, file, new BinaryDocument(bytes, edits?.History, edits?.Redo));
    }

    private async Task<EditState> SaveDocumentAsync(Project project, ProjectFile file, BinaryDocument doc, CancellationToken ct)
    {
        var fresh = await GetProjectAsync(project.Id, ct);
        var others = fresh.Edits.Where(e => e.FileId != file.Id);
        var edits = doc.History.Count + doc.RedoStack.Count == 0 ? others.ToList() : [.. others, new FileEdits(file.Id, doc.History.ToList(), doc.RedoStack.ToList())];
        await store.SaveAsync(fresh with { Edits = edits, UpdatedAt = DateTimeOffset.UtcNow }, ct);
        return await StateOf(fresh, file, doc, ct);
    }

    private async Task<EditState> StateOf(Project project, ProjectFile file, BinaryDocument doc, CancellationToken ct)
    {
        var maps = await TryMapDefinitionsAsync(project, file, ct);
        var ranges = doc.ChangedRanges();
        return new EditState
        {
            FileId = file.Id, Size = doc.Length, OriginalSha256 = doc.OriginalSha256, WorkingSha256 = doc.WorkingSha256,
            ChangedBytes = doc.ChangedBytes(), ChangedRanges = ranges, RedoCount = doc.RedoStack.Count,
            History = doc.History.Select(o => new EditSummary(o.Id, o.At, o.Source, o.MapId, o.Description, o.ChangedBytes, o.Patches.Count > 0 ? o.Patches[0].Address : 0)).Reverse().ToList(),
            Regions = ranges.Select(r =>
            {
                var m = maps?.FirstOrDefault(d => r.Start < d.Address + d.ByteLength && d.Address < r.Start + r.Length);
                return new ChangedRegion(r.Start, r.Length, m?.Id, m?.Name);
            }).ToList(),
        };
    }

    /// <summary>Map definitions that apply to this file, from an analysis in which it took part (same SHA-256).</summary>
    private async Task<IReadOnlyList<MapDefinition>?> TryMapDefinitionsAsync(Project project, ProjectFile file, CancellationToken ct)
    {
        foreach (var id in new[] { file.Summary?.AnalysisId, project.LatestAnalysisId }.OfType<Guid>().Distinct())
        {
            AnalysisSession s;
            try { s = await GetSessionAsync(id, ct); }
            catch (EcuStudioException) { continue; }
            if (s.Report.ModifiedSha256 == file.Sha256) return s.ModCalibration.Maps.Select(m => m.Definition).ToList();
            if (s.Report.StockSha256 == file.Sha256 && s.StockCalibration is { } sc) return sc.Maps.Select(m => m.Definition).ToList();
        }
        return null;
    }

    private async Task<MapDefinition> MapDefinitionAsync(Project project, ProjectFile file, string mapId, CancellationToken ct)
    {
        var maps = await TryMapDefinitionsAsync(project, file, ct)
            ?? throw new EcuStudioException("ANALYSIS_REQUIRED", $"Run an analysis that includes {file.Name} first: map definitions come from it");
        return maps.FirstOrDefault(m => m.Id == mapId) ?? throw new NotFoundException($"Map {mapId} is not defined for {file.Name}");
    }

    public async Task<EditState> GetEditStateAsync(Guid projectId, Guid fileId, CancellationToken ct = default)
    {
        var (project, file, doc) = await LoadDocumentAsync(projectId, fileId, ct);
        return await StateOf(project, file, doc, ct);
    }

    public async Task<WorkingHexPage> GetWorkingHexAsync(Guid projectId, Guid fileId, int offset, int length, CancellationToken ct = default)
    {
        var (_, _, doc) = await LoadDocumentAsync(projectId, fileId, ct);
        if (offset < 0 || offset >= doc.Length) throw new EcuStudioException("INVALID_RANGE", $"Offset 0x{offset:X} is outside the file");
        length = Math.Clamp(length, 1, Math.Min(64 * 1024, doc.Length - offset));
        var changed = doc.ChangedRanges().Where(r => r.Start < offset + length && offset < r.Start + r.Length).ToList();
        return new WorkingHexPage(offset, length, doc.Length, doc.Working.Slice(offset, length).ToArray(), doc.Original.Slice(offset, length).ToArray(), changed);
    }

    public async Task<EditState> ApplyHexEditAsync(Guid projectId, Guid fileId, HexEdit edit, CancellationToken ct = default)
    {
        byte[] bytes;
        try { bytes = Convert.FromHexString(edit.Hex.Replace(" ", "", StringComparison.Ordinal)); }
        catch (FormatException) { throw new BinaryEditException("Hex must be pairs of hexadecimal digits"); }
        if (bytes.Length is 0 or > 4096) throw new BinaryEditException("A hex edit writes 1–4096 bytes");
        var (project, file, doc) = await LoadDocumentAsync(projectId, fileId, ct);
        doc.Apply("hex", edit.Description ?? $"Hex edit at 0x{edit.Address:X} ({bytes.Length} byte(s))", [new ByteWrite(edit.Address, bytes)]);
        return await SaveDocumentAsync(project, file, doc, ct);
    }

    public async Task<MapEditPreview> PreviewMapEditAsync(Guid projectId, Guid fileId, MapEditRequest req, CancellationToken ct = default)
    {
        var (project, file, doc) = await LoadDocumentAsync(projectId, fileId, ct);
        var def = await MapDefinitionAsync(project, file, req.MapId, ct);
        using var working = BinaryImage.FromBytes(doc.WorkingCopy(), file.Name);
        return MapEditor.Preview(MapDecoder.Decode(working, def), req.Operation);
    }

    public async Task<EditState> ApplyMapEditAsync(Guid projectId, Guid fileId, MapEditRequest req, CancellationToken ct = default)
    {
        var (project, file, doc) = await LoadDocumentAsync(projectId, fileId, ct);
        var def = await MapDefinitionAsync(project, file, req.MapId, ct);
        MapEditPreview preview;
        using (var working = BinaryImage.FromBytes(doc.WorkingCopy(), file.Name))
            preview = MapEditor.Preview(MapDecoder.Decode(working, def), req.Operation);
        var description = req.Description ?? $"{def.Name}: {req.Operation.Kind}{(req.Operation.Kind is MapOperationKind.Set or MapOperationKind.Add or MapOperationKind.Multiply or MapOperationKind.Percent ? $" {req.Operation.Operand:0.###}" : "")} on {preview.Changes.Count} cell(s)";
        doc.Apply("map", description, preview.Writes, def.Id);
        return await SaveDocumentAsync(project, file, doc, ct);
    }

    public async Task<EditState> UndoAsync(Guid projectId, Guid fileId, CancellationToken ct = default)
    {
        var (project, file, doc) = await LoadDocumentAsync(projectId, fileId, ct);
        if (doc.Undo() is null) throw new BinaryEditException("Nothing to undo");
        return await SaveDocumentAsync(project, file, doc, ct);
    }

    public async Task<EditState> RedoAsync(Guid projectId, Guid fileId, CancellationToken ct = default)
    {
        var (project, file, doc) = await LoadDocumentAsync(projectId, fileId, ct);
        if (doc.Redo() is null) throw new BinaryEditException("Nothing to redo");
        return await SaveDocumentAsync(project, file, doc, ct);
    }

    public async Task<EditState> RevertAsync(Guid projectId, Guid fileId, RevertRequest req, CancellationToken ct = default)
    {
        var (project, file, doc) = await LoadDocumentAsync(projectId, fileId, ct);
        if (req.All) doc.RevertAll();
        else if (req.MapId is { } mapId)
        {
            var def = await MapDefinitionAsync(project, file, mapId, ct);
            doc.Revert([MapEditor.ValueRange(def)], $"Revert map {def.Name}", def.Id);
        }
        else if (req.Ranges is { Count: > 0 } ranges) doc.Revert(ranges, $"Revert {ranges.Sum(r => r.Length)} byte(s)");
        else throw new BinaryEditException("Say what to revert: ranges, a map or all");
        return await SaveDocumentAsync(project, file, doc, ct);
    }

    // ---------------- safe save -----------------------------------------------------------

    public async Task<SaveCheck> CheckSaveAsync(Guid projectId, Guid fileId, SaveRequest? req = null, CancellationToken ct = default)
    {
        var (project, file, doc) = await LoadDocumentAsync(projectId, fileId, ct);
        return (await BuildSaveCheckAsync(project, file, doc, req ?? new SaveRequest(null), ct)).Check;
    }

    private async Task<(SaveCheck Check, byte[] Output)> BuildSaveCheckAsync(Project project, ProjectFile file, BinaryDocument doc, SaveRequest req, CancellationToken ct)
    {
        var checks = new List<SaveCheckItem>();
        var output = doc.WorkingCopy();
        var original = doc.Original.ToArray();

        checks.Add(doc.IsModified
            ? new("changes", SaveCheckStatus.Pass, $"{doc.ChangedBytes()} byte(s) changed in {doc.ChangedRanges().Count} range(s)")
            : new("changes", SaveCheckStatus.Fail, "No changes to save"));
        checks.Add(output.Length == doc.Length && doc.Length == file.Size
            ? new("size", SaveCheckStatus.Pass, $"Size unchanged: {doc.Length} bytes")
            : new("size", SaveCheckStatus.Fail, $"Size differs from the original ({file.Size} bytes)"));
        checks.Add(doc.OriginalSha256 == file.Sha256
            ? new("original", SaveCheckStatus.Pass, "Stored original is intact (SHA-256 matches)")
            : new("original", SaveCheckStatus.Fail, "Stored original does not match its recorded SHA-256"));
        checks.Add(new("offsets", SaveCheckStatus.Pass, "Edits overwrite bytes in place: nothing was inserted, removed or moved, padding is untouched unless edited"));

        // Where the changes are.
        using var image = BinaryImage.FromBytes((byte[])original.Clone(), file.Name);
        var plugin = plugins.Detect(image).Plugin;
        var ident = plugin.Identify(image);
        var maps = await TryMapDefinitionsAsync(project, file, ct) ?? [];
        var resolution = plugin.ResolveDefinitions(image, ident, await ProjectExternalDefinitionAsync(project, image, ct));
        var specs = resolution.Checksums;
        var checksumFields = specs.Select(s => new ByteRange(s.StoredAt, s.EffectiveStoreSize)).ToList();
        int outsideMaps = 0, inCode = 0, inPadding = 0;
        foreach (var r in doc.ChangedRanges())
        {
            for (var a = r.Start; a < r.Start + r.Length; a++)
            {
                var inMap = maps.Any(d => a >= d.Address && a < d.Address + d.ByteLength
                    || d.XAxis?.Address is { } xa && a >= xa && a < xa + d.XAxis.Length * d.XAxis.DataType.Size()
                    || d.YAxis?.Address is { } ya && a >= ya && a < ya + d.YAxis.Length * d.YAxis.DataType.Size());
                if (!inMap && !checksumFields.Any(c => a >= c.Start && a < c.Start + c.Length)) outsideMaps++;
                if (ident.Sections.Any(s => s.Kind is SectionKind.Code or SectionKind.Boot && a >= s.Start && a < s.End)) inCode++;
                if (InPadding(original, a)) inPadding++;
            }
        }
        checks.Add(outsideMaps == 0
            ? new("regions", SaveCheckStatus.Pass, "All changes are inside known maps or checksum fields")
            : new("regions", SaveCheckStatus.Warn, $"{outsideMaps} changed byte(s) lie outside known maps"));
        var needs = new List<string>();
        if (inCode > 0)
        {
            checks.Add(new("code", req.AcknowledgeCodeChanges ? SaveCheckStatus.Warn : SaveCheckStatus.Fail, $"{inCode} changed byte(s) lie in a code/boot section: unexpected program modification"));
            if (!req.AcknowledgeCodeChanges) needs.Add("code");
        }
        else checks.Add(new("code", SaveCheckStatus.Pass, ident.Sections.Count == 0 ? "No code sections are identified for this file" : "No changes in code or boot sections"));
        if (inPadding > 0) checks.Add(new("padding", SaveCheckStatus.Warn, $"{inPadding} changed byte(s) were in empty/padding areas of the original"));

        var checksum = plugin.CorrectChecksums(output, specs);
        var safe = checksum.Overall is ChecksumStatus.Valid or ChecksumStatus.Corrected;
        checks.Add(new("checksum", safe ? SaveCheckStatus.Pass : req.AcknowledgeChecksumRisk ? SaveCheckStatus.Warn : SaveCheckStatus.Fail,
            safe ? checksum.Note : $"Checksum {checksum.Overall}: {checksum.Note} The saved file is for analysis and must not be written to an ECU as is."));
        if (!safe && !req.AcknowledgeChecksumRisk) needs.Add("checksum");

        var canSave = checks.All(c => c.Status != SaveCheckStatus.Fail);
        return (new SaveCheck { CanSave = canSave, Checks = checks, Checksum = checksum, SuggestedName = NextVersionName(project, file), NeedsAcknowledgement = needs }, output);
    }

    /// <summary>Saves the working buffer (with described checksums recalculated) as a NEW project file, then verifies it.</summary>
    public async Task<SaveResult> SaveAsNewFileAsync(Guid projectId, Guid fileId, SaveRequest req, CancellationToken ct = default)
    {
        var (project, file, doc) = await LoadDocumentAsync(projectId, fileId, ct);
        var (check, output) = await BuildSaveCheckAsync(project, file, doc, req, ct);
        if (!check.CanSave)
            throw new EcuStudioException("SAVE_BLOCKED", string.Join("; ", check.Checks.Where(c => c.Status == SaveCheckStatus.Fail).Select(c => c.Message)), 409,
                new Dictionary<string, object?> { ["check"] = check });
        var name = SanitizeName(req.Name) ?? check.SuggestedName;
        if (project.Files.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new EcuStudioException("DUPLICATE_NAME", $"A file named {name} already exists in the project");
        var newFile = new ProjectFile
        {
            Id = Guid.NewGuid(), Name = name, Label = Path.GetFileNameWithoutExtension(name), Role = FileRole.Version,
            Sha256 = Hashing.Sha256Hex(output), Size = output.Length,
            Notes = $"Saved from {file.Name}: {doc.ChangedBytes()} byte(s) edited, checksum {check.Checksum.Overall}",
        };
        await store.SaveFileContentAsync(newFile.Id, output, ct);
        var fresh = await GetProjectAsync(projectId, ct);
        await store.SaveAsync(fresh with { Files = [.. fresh.Files, newFile], UpdatedAt = DateTimeOffset.UtcNow }, ct);

        // Read back and verify what was stored.
        var stored = await store.GetFileContentAsync(newFile.Id, ct) ?? [];
        var verification = new List<string>();
        var ok = stored.Length == doc.Length && Hashing.Sha256Hex(stored) == newFile.Sha256;
        verification.Add(ok ? $"Read back {stored.Length} bytes, SHA-256 {newFile.Sha256[..12]}… matches" : "Stored file does not match what was written");
        var outsideEdits = 0;
        var edited = doc.ChangedRanges();
        var checksumFields = check.Checksum.Blocks.Count;
        for (var i = 0; i < Math.Min(stored.Length, doc.Length); i++)
            if (stored[i] != doc.Original[i] && !edited.Any(r => i >= r.Start && i < r.Start + r.Length)) outsideEdits++;
        verification.Add(outsideEdits == 0 ? "No byte changed outside the edits" : $"{outsideEdits} byte(s) outside the edits differ (checksum fields of {checksumFields} described block(s))");
        var origAfter = await store.GetFileContentAsync(file.Id, ct);
        var originalIntact = origAfter is not null && Hashing.Sha256Hex(origAfter) == file.Sha256;
        verification.Add(originalIntact ? $"Original {file.Name} is unchanged" : $"Original {file.Name} changed: this must not happen");
        return new SaveResult(newFile, check, ok && originalIntact, verification);
    }

    public async Task<(string Name, byte[] Content)> GetFileContentAsync(Guid projectId, Guid fileId, CancellationToken ct = default)
    {
        var project = await GetProjectAsync(projectId, ct);
        var file = project.Files.FirstOrDefault(f => f.Id == fileId) ?? throw new NotFoundException($"File {fileId} not found in project");
        return (file.Name, await store.GetFileContentAsync(file.Id, ct) ?? throw new NotFoundException($"Content for file {file.Name} missing"));
    }

    private async Task<Calibration.Plugins.ExternalDefinition?> ProjectExternalDefinitionAsync(Project project, BinaryImage image, CancellationToken ct)
    {
        var content = project.Definition is { Origin: ECUStudio.Application.Library.DefinitionOrigin.Upload } pd ? await store.GetFileContentAsync(pd.Id, ct) : null;
        return definitions.Resolve(project.Definition, _ => content, image).Definition;
    }

    private static bool InPadding(ReadOnlySpan<byte> data, int address)
    {
        // Inside a run of ≥ 64 identical 0x00/0xFF bytes.
        var b = data[address];
        if (b is not (0x00 or 0xFF)) return false;
        int lo = address, hi = address;
        while (lo > 0 && data[lo - 1] == b && address - lo < 64) lo--;
        while (hi < data.Length - 1 && data[hi + 1] == b && hi - lo < 64) hi++;
        return hi - lo + 1 >= 64;
    }

    private static string NextVersionName(Project project, ProjectFile file)
    {
        var baseName = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(file.Name), @"_v\d+$", "");
        var ext = Path.GetExtension(file.Name) is { Length: > 0 } e ? e : ".bin";
        for (var n = 1; ; n++)
        {
            var name = $"{baseName}_v{n}{ext}";
            if (!project.Files.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    private static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var clean = Path.GetFileName(name.Trim());
        if (clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || clean is "." or "..") throw new EcuStudioException("INVALID_NAME", "Invalid file name");
        return Path.HasExtension(clean) ? clean : clean + ".bin";
    }
}

using System.Text.Json.Serialization;
using ECUStudio.Application.Projects;
using ECUStudio.Binary;
using ECUStudio.Calibration.Library;
using ECUStudio.Core;

namespace ECUStudio.Application.Analysis;

[JsonConverter(typeof(JsonStringEnumConverter<StockCandidateOrigin>))]
public enum StockCandidateOrigin { Project, Library }

/// <summary>
/// A binary that may be the stock (original) calibration of the analysed file. It is only called "same software"
/// when the SW number matches; even then it is a candidate until the user assigns it the Stock role.
/// </summary>
public sealed record StockCandidate
{
    public required StockCandidateOrigin Origin { get; init; }
    public required string Name { get; init; }
    public Guid? FileId { get; init; }
    public string? LibraryEntryId { get; init; }
    public required MatchLevel Level { get; init; }
    public required bool SameSoftware { get; init; }
    public bool SizeMatches { get; init; }
    /// <summary>Bytes differing from the analysed file (when both are readable and of equal size).</summary>
    public int? DifferingBytes { get; init; }
    public bool Available { get; init; } = true;
    public IReadOnlyList<string> Reasons { get; init; } = [];
}

public sealed record AddFromLibraryBody(string EntryId, FileRole? Role, string? Label);

public sealed partial class StudioService
{
    /// <summary>Stock candidates for an analysis: project files and library dumps with the same SW/HW, closest first.</summary>
    public async Task<IReadOnlyList<StockCandidate>> StockCandidatesAsync(Guid analysisId, CancellationToken ct = default)
    {
        var s = await GetSessionAsync(analysisId, ct);
        var mod = s.Request.Modified;
        var ident = s.Report.Ecu;
        string? sw = ident.SoftwareNumber.IsKnown ? ident.SoftwareNumber.Text : null;
        string? hw = ident.HardwareNumber.IsKnown ? ident.HardwareNumber.Text : null;
        var result = new List<StockCandidate>();
        var projectShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (s.Report.ProjectId is { } pid && await store.GetAsync(pid, ct) is { } project)
        {
            foreach (var f in project.Files.Where(f => f.Sha256 != mod.Sha256))
            {
                projectShas.Add(f.Sha256);
                var bytes = await store.GetFileContentAsync(f.Id, ct);
                if (bytes is null) continue;
                using var img = BinaryImage.FromBytes(bytes, f.Name);
                var key = definitions.KeyOf(img);
                var sameSw = sw is not null && key.SoftwareNumber == sw;
                var sameHw = hw is not null && key.HardwareNumber == hw;
                var reasons = new List<string>();
                if (sameSw) reasons.Add($"SW {sw} matches");
                else reasons.Add(key.SoftwareNumber is null ? "SW number not readable" : $"different SW {key.SoftwareNumber}");
                if (sameHw) reasons.Add($"HW {hw} matches");
                if (f.Role == FileRole.Stock) reasons.Add("assigned the Stock role in this project");
                var level = sameSw && sameHw ? MatchLevel.Exact : sameSw ? MatchLevel.Strong : key.EcuFamily == ident.EcuFamily ? MatchLevel.Weak : MatchLevel.Unknown;
                if (level == MatchLevel.Unknown) continue;
                result.Add(new StockCandidate
                {
                    Origin = StockCandidateOrigin.Project, Name = f.Name, FileId = f.Id, Level = level, SameSoftware = sameSw,
                    SizeMatches = bytes.Length == mod.Length, DifferingBytes = bytes.Length == mod.Length ? BinaryDiff.CountChangedBytes(bytes, mod.Span) : null, Reasons = reasons,
                });
            }
        }

        var matches = definitions.Library.Match(new BinaryKey(sw, hw, ident.OemPartNumber.IsKnown ? ident.OemPartNumber.Text : null, ident.EcuFamily, mod.Sha256, null), 200)
            .Where(m => m.Entry.Format is LibraryFormat.Binary && m.Level <= MatchLevel.Probable);
        foreach (var m in matches)
        {
            var sameSw = sw is not null && m.Entry.Identifiers.SoftwareNumbers.Contains(sw);
            var identical = m.Entry.Sha256 is { } sha && string.Equals(sha, mod.Sha256, StringComparison.OrdinalIgnoreCase);
            if (identical) continue; // the analysed file itself
            if (m.Entry.Sha256 is { } esha && projectShas.Contains(esha)) continue; // already in the project
            int? diff = null;
            if (m.Entry.Available && m.Entry.Size == mod.Length)
            {
                try { diff = BinaryDiff.CountChangedBytes(definitions.Library.Read(m.Entry.Id).Content, mod.Span); }
                catch (EcuStudioException) { }
            }
            result.Add(new StockCandidate
            {
                Origin = StockCandidateOrigin.Library, Name = m.Entry.RelativePath, LibraryEntryId = m.Entry.Id, Level = m.Level, SameSoftware = sameSw,
                SizeMatches = m.Entry.Size == mod.Length, DifferingBytes = diff, Available = m.Entry.Available, Reasons = m.Reasons,
            });
        }
        return result.OrderBy(c => c.Level).ThenByDescending(c => c.SizeMatches).ThenBy(c => c.DifferingBytes ?? int.MaxValue).ToList();
    }

    /// <summary>Adds a library binary to the project (the user's explicit action; the archive file itself is untouched).</summary>
    public async Task<ProjectFile> AddFileFromLibraryAsync(Guid projectId, AddFromLibraryBody body, CancellationToken ct = default)
    {
        var (entry, content) = definitions.Library.Read(body.EntryId);
        if (entry.Format is not (LibraryFormat.Binary)) throw new EcuStudioException("NOT_A_BINARY", $"{entry.RelativePath} is not a binary dump");
        if (content.Length > BinaryImage.MaxSupportedSize) throw new InvalidBinaryException("File too large");
        return await AddFileAsync(projectId, Path.GetFileName(entry.RelativePath), content, body.Role ?? FileRole.Version, body.Label, $"From library: {entry.RelativePath}", ct);
    }
}

using System.Security.Cryptography;
using ECUStudio.Application.Analysis;
using ECUStudio.Application.Projects;
using ECUStudio.Binary;
using ECUStudio.Binary.Editing;
using ECUStudio.Calibration.Model;
using ECUStudio.Core;
using ECUStudio.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ECUStudio.Tests;

/// <summary>Data-safety guarantees of the editor: a binary is never corrupted, shifted or resized.</summary>
public class BinarySafetyTests
{
    private static byte[] Stock => Fixtures.Stock.Value.Image;

    [Fact]
    public void Untouched_document_round_trips_byte_identical()
    {
        var doc = new BinaryDocument(Stock);
        Assert.Equal(Hashing.Sha256Hex(Stock), doc.WorkingSha256);
        Assert.Equal(doc.OriginalSha256, doc.WorkingSha256);
        Assert.False(doc.IsModified);
        Assert.Empty(doc.ChangedRanges());
    }

    [Fact]
    public void Edits_change_only_the_written_bytes_and_full_undo_is_byte_identical()
    {
        var original = (byte[])Stock.Clone();
        var doc = new BinaryDocument(original);
        doc.Apply("hex", "a", [new ByteWrite(0x50010, [0x12, 0x34])]);
        doc.Apply("hex", "b", [new ByteWrite(0x50100, [0xAB]), new ByteWrite(0x60000, [0x00, 0x01, 0x02])]);
        doc.Apply("hex", "c", [new ByteWrite(0x50010, [0x55])]);

        Assert.Equal(original.Length, doc.Length);
        Assert.Equal(original, Stock); // the caller's array is never touched
        var changed = Enumerable.Range(0, doc.Length).Where(i => doc.Working[i] != original[i]).ToHashSet();
        var expected = new[] { 0x50010, 0x50011, 0x50100, 0x60000, 0x60001, 0x60002 }.Where(a => original[a] != doc.Working[a]).ToHashSet();
        Assert.Equal(expected, changed);

        while (doc.Undo() is not null) { }
        Assert.Equal(doc.OriginalSha256, doc.WorkingSha256);
        while (doc.Redo() is not null) { }
        doc.RevertAll();
        Assert.Equal(doc.OriginalSha256, doc.WorkingSha256);
    }

    [Fact]
    public void Out_of_range_writes_are_rejected_and_leave_the_buffer_untouched()
    {
        var doc = new BinaryDocument(Stock);
        Assert.Throws<BinaryEditException>(() => doc.Apply("hex", "x", [new ByteWrite(0x100, [1]), new ByteWrite(doc.Length - 1, [1, 2])]));
        Assert.Throws<BinaryEditException>(() => doc.Apply("hex", "x", [new ByteWrite(-1, [1])]));
        Assert.False(doc.IsModified);
        Assert.Empty(doc.History);
    }

    [Fact]
    public void History_replayed_from_storage_must_match_the_bytes()
    {
        var doc = new BinaryDocument(Stock);
        var op = doc.Apply("hex", "a", [new ByteWrite(0x50010, [(byte)(Stock[0x50010] ^ 0xFF)])])!;
        Assert.Equal(doc.WorkingSha256, new BinaryDocument(Stock, [op]).WorkingSha256);

        var forged = op with { Patches = [op.Patches[0] with { Old = [(byte)(Stock[0x50010] ^ 0x01)] }] };
        Assert.Throws<BinaryEditException>(() => new BinaryDocument(Stock, [forged]));
    }

    [Fact]
    public void Same_file_compared_with_itself_has_no_differences()
    {
        Assert.Empty(BinaryDiff.ChangedRanges(Stock, Stock));
        Assert.Equal(0, BinaryDiff.CountChangedBytes(Stock, Stock));
    }

    [Fact]
    public void Checksum_correction_rewrites_only_the_described_stored_values()
    {
        var specs = Fixtures.Stock.Value.Definition.ToChecksumSpecs();
        var data = (byte[])Stock.Clone();
        data[0x50020] ^= 0x01;
        Assert.Equal(ChecksumStatus.Invalid, ChecksumVerifier.Verify(data, specs, "t").Overall);
        var before = (byte[])data.Clone();

        var report = ChecksumVerifier.Correct(data, specs, "t");

        Assert.Equal(ChecksumStatus.Corrected, report.Overall);
        Assert.Equal(ChecksumStatus.Valid, ChecksumVerifier.Verify(data, specs, "t").Overall);
        var changed = Enumerable.Range(0, data.Length).Where(i => data[i] != before[i]).ToList();
        Assert.All(changed, i => Assert.InRange(i, 0x7FFF4, 0x7FFFB)); // the two calibration sums; code CRC untouched
        Assert.Equal(ChecksumStatus.Unsupported, ChecksumVerifier.Correct(data, [], "t").Overall);
    }

    private static (ServiceProvider Sp, StudioService Studio) Services()
    {
        var sp = new ServiceCollection().AddEcuStudio(new EcuStudioOptions { Storage = "memory", AnthropicApiKey = null }).BuildServiceProvider();
        return (sp, sp.GetRequiredService<StudioService>());
    }

    private static async Task<(Project Project, ProjectFile File, AnalysisSession Session)> AnalysedStock(StudioService studio)
    {
        var project = await studio.CreateProjectAsync("edit", null, null);
        var file = await studio.AddFileAsync(project.Id, "stock.bin", Stock, FileRole.Stock, null, null);
        var session = await studio.RunAnalysisAsync(await studio.GetProjectAsync(project.Id), file, null, null, CancellationToken.None);
        return (await studio.GetProjectAsync(project.Id), file, session);
    }

    [Fact]
    public async Task Single_cell_map_edit_changes_two_value_bytes_plus_checksums_and_saves_a_new_file()
    {
        var (sp, studio) = Services();
        await using var _ = sp;
        var (project, file, session) = await AnalysedStock(studio);
        var map = session.ModCalibration.Maps.First(m => m.Definition.Id == "torque_limiter");
        var cell = new Cell(0, 2);
        var oldValue = map.Values[cell.Row * map.Definition.Cols + cell.Col];
        var request = new MapEditRequest("torque_limiter", new MapOperation(MapOperationKind.Values, [cell], Values: [oldValue + 10]));

        var preview = await studio.PreviewMapEditAsync(project.Id, file.Id, request);
        var change = Assert.Single(preview.Changes);
        Assert.Equal(map.Definition.Address + cell.Col * 2, change.Address);
        Assert.Equal(oldValue + 10, change.Stored, 6);
        Assert.False((await studio.GetEditStateAsync(project.Id, file.Id)).CanUndo); // preview does not apply

        var state = await studio.ApplyMapEditAsync(project.Id, file.Id, request);
        Assert.InRange(state.ChangedBytes, 1, 2); // only bytes whose value actually differs
        Assert.Equal("torque_limiter", Assert.Single(state.Regions).MapId);

        var check = await studio.CheckSaveAsync(project.Id, file.Id);
        Assert.True(check.CanSave);
        Assert.Equal(ChecksumStatus.Corrected, check.Checksum.Overall);
        Assert.Equal("stock_v1.bin", check.SuggestedName);

        var saved = await studio.SaveAsNewFileAsync(project.Id, file.Id, new SaveRequest(null));
        Assert.True(saved.Verified, string.Join("; ", saved.Verification));
        Assert.Equal(FileRole.Version, saved.File.Role);
        var (_, output) = await studio.GetFileContentAsync(project.Id, saved.File.Id);
        Assert.Equal(Stock.Length, output.Length);
        var diff = Enumerable.Range(0, Stock.Length).Where(i => output[i] != Stock[i]).ToList();
        Assert.All(diff, i => Assert.True(i == change.Address || i == change.Address + 1 || i is >= 0x7FFF4 and <= 0x7FFFB, $"unexpected change at 0x{i:X}"));
        var (_, originalAfter) = await studio.GetFileContentAsync(project.Id, file.Id);
        Assert.Equal(SHA256.HashData(Stock), SHA256.HashData(originalAfter));

        // Undo of the edit returns the working buffer to the original exactly.
        var undone = await studio.UndoAsync(project.Id, file.Id);
        Assert.Equal(undone.OriginalSha256, undone.WorkingSha256);
        Assert.Equal(1, undone.RedoCount);
    }

    [Fact]
    public async Task Saving_without_changes_or_without_known_checksums_is_blocked_until_acknowledged()
    {
        var (sp, studio) = Services();
        await using var _ = sp;
        var (project, file, _) = await AnalysedStock(studio);
        var nothing = await studio.CheckSaveAsync(project.Id, file.Id);
        Assert.False(nothing.CanSave);

        await studio.ApplyHexEditAsync(project.Id, file.Id, new HexEdit(0x50010, "0102"));
        await studio.RevertAsync(project.Id, file.Id, new RevertRequest(All: true));
        Assert.False((await studio.CheckSaveAsync(project.Id, file.Id)).CanSave);

        // A file without described checksum blocks: Unsupported, never presented as safe.
        var noChecksums = (byte[])Stock.Clone();
        var sw = System.Text.Encoding.ASCII.GetBytes("1037399999");
        var at = noChecksums.AsSpan().IndexOf(sw);
        Assert.True(at > 0);
        noChecksums[at + 9] = (byte)'8'; // different SW number → no definition, no checksum blocks
        var other = await studio.AddFileAsync(project.Id, "other.bin", noChecksums, FileRole.Modified, null, null);
        await studio.ApplyHexEditAsync(project.Id, other.Id, new HexEdit(0x50010, "0102"));
        var check = await studio.CheckSaveAsync(project.Id, other.Id);
        Assert.False(check.CanSave);
        Assert.Equal(ChecksumStatus.Unsupported, check.Checksum.Overall);
        Assert.Contains("checksum", check.NeedsAcknowledgement);
        await Assert.ThrowsAsync<EcuStudioException>(() => studio.SaveAsNewFileAsync(project.Id, other.Id, new SaveRequest(null)));
        var saved = await studio.SaveAsNewFileAsync(project.Id, other.Id, new SaveRequest("other_v1", AcknowledgeChecksumRisk: true));
        Assert.Equal("other_v1.bin", saved.File.Name);
        Assert.Contains(saved.Check.Checks, c => c.Id == "checksum" && c.Status == SaveCheckStatus.Warn);
    }

    /// <summary>Real binary from the user's machine, used read-only when present (never committed, never modified).</summary>
    [Fact]
    public void Local_real_binary_round_trips_unchanged_when_available()
    {
        var path = Environment.GetEnvironmentVariable("ECUSTUDIO_REAL_BIN");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var doc = new BinaryDocument(File.ReadAllBytes(path));
        doc.Apply("hex", "probe", [new ByteWrite(doc.Length / 2, [(byte)(doc.Working[doc.Length / 2] ^ 0xFF)])]);
        doc.Undo();
        Assert.Equal(doc.OriginalSha256, doc.WorkingSha256);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
    }
}

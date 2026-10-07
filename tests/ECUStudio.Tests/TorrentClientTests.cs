using System.Net;
using System.Net.Sockets;
using ECUStudio.Application.Acquisition;
using ECUStudio.Calibration.Library;
using ECUStudio.Infrastructure.Torrents;
using MonoTorrent;
using MonoTorrent.Client;
using Xunit;

namespace ECUStudio.Tests;

public class TorrentClientTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "ecus-tor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task Selective_download_fetches_only_the_requested_file_over_loopback()
    {
        var root = TempDir();
        try
        {
            // Archive with three files; only the middle one is requested.
            var seedDir = Path.Combine(root, "seed");
            var archive = Path.Combine(seedDir, "Archive");
            Directory.CreateDirectory(Path.Combine(archive, "EDC16", "HAXE"));
            var rnd = new Random(7);
            byte[] Bytes(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
            var first = Bytes(700_000); var wanted = Bytes(1_300_000); var last = Bytes(900_000);
            File.WriteAllBytes(Path.Combine(archive, "a_first.bin"), first);
            File.WriteAllBytes(Path.Combine(archive, "EDC16", "HAXE", "b_wanted.a2l"), wanted);
            File.WriteAllBytes(Path.Combine(archive, "c_last.bin"), last);
            Directory.CreateDirectory(Path.Combine(archive, "notes"));
            File.WriteAllBytes(Path.Combine(archive, "notes", "empty.txt"), []);
            var torrentBytes = (await new TorrentCreator().CreateAsync(new TorrentFileSource(archive))).Encode();

            var meta = TorrentMetadata.Parse(torrentBytes);
            var wantedPath = meta.Files.Single(f => f.Path.EndsWith("b_wanted.a2l", StringComparison.Ordinal)).Path;
            Assert.Equal("Archive/EDC16/HAXE/b_wanted.a2l", wantedPath);

            var seedPort = FreePort();
            using var seeder = new ClientEngine(new EngineSettingsBuilder
            {
                CacheDirectory = Path.Combine(root, "seed-state"), AllowPortForwarding = false, AllowLocalPeerDiscovery = false, DhtEndPoint = null,
                AutoSaveLoadFastResume = false, ListenEndPoints = new() { ["ipv4"] = new IPEndPoint(IPAddress.Loopback, seedPort) },
            }.ToSettings());
            var seed = await seeder.AddAsync(Torrent.Load(torrentBytes), seedDir, new TorrentSettingsBuilder { CreateContainingDirectory = true }.ToSettings());
            await seed.HashCheckAsync(true);

            await using var client = new MonoTorrentClient(new MonoTorrentOptions
            {
                StateDirectory = Path.Combine(root, "state"), UseDht = false, ListenPort = FreePort(), MaxUploadBytesPerSecond = 0,
                ExtraPeers = [new IPEndPoint(IPAddress.Loopback, seedPort)], StallTimeout = TimeSpan.FromSeconds(30),
            });
            var save = Path.Combine(root, "download");
            var reports = new List<TorrentTransferProgress>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await client.DownloadAsync(new TorrentDownloadRequest(torrentBytes, save, [wantedPath]), new Progress(reports.Add), cts.Token);

            var got = Path.Combine(save, "Archive", "EDC16", "HAXE", "b_wanted.a2l");
            Assert.True(File.Exists(got));
            Assert.Equal(wanted, File.ReadAllBytes(got));
            Assert.Equal(TransferPhase.Done, reports[^1].Phase);
            Assert.Equal(wanted.Length, reports[^1].BytesTotal);
            // Neighbouring files are at most touched by shared boundary pieces, never completed.
            foreach (var other in new[] { ("a_first.bin", first), ("c_last.bin", last) })
            {
                var p = Path.Combine(save, "Archive", other.Item1);
                Assert.False(File.Exists(p) && File.ReadAllBytes(p).AsSpan().SequenceEqual(other.Item2), $"{other.Item1} was downloaded");
            }
            // Zero-length files MonoTorrent creates on start are removed again.
            Assert.False(Directory.Exists(Path.Combine(save, "Archive", "notes")));
            await seeder.StopAllAsync();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Requesting_a_file_outside_the_torrent_fails()
    {
        var root = TempDir();
        try
        {
            var dir = Path.Combine(root, "Arch");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "x.bin"), new byte[1000]);
            File.WriteAllBytes(Path.Combine(dir, "y.bin"), new byte[1000]);
            var bytes = new TorrentCreator().Create(new TorrentFileSource(dir)).Encode();
            var client = new MonoTorrentClient(new MonoTorrentOptions { StateDirectory = Path.Combine(root, "s"), UseDht = false, ListenPort = FreePort() });
            var ex = Assert.ThrowsAsync<Core.EcuStudioException>(() => client.DownloadAsync(new TorrentDownloadRequest(bytes, Path.Combine(root, "d"), ["Arch/nope.bin"]), null, CancellationToken.None)).GetAwaiter().GetResult();
            Assert.Equal("TORRENT_FILE_MISSING", ex.Code);
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void Info_hash_matches_MonoTorrent()
    {
        var root = TempDir();
        try
        {
            var dir = Path.Combine(root, "Arch");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "x.bin"), new byte[5000]);
            var bytes = new TorrentCreator().Create(new TorrentFileSource(dir)).Encode();
            Assert.Equal(Torrent.Load(bytes).InfoHashes.V1!.ToHex().ToLowerInvariant(), TorrentMetadata.Parse(bytes).InfoHash);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:C12FE1C06BBA254A9DC9F519B335AA7C1367A88A&dn=Damos+GIFROM", "c12fe1c06bba254a9dc9f519b335aa7c1367a88a", "Damos GIFROM")]
    [InlineData("magnet:?xt=urn:btih:YEX6DQDLXISUVHOJ6UM3GNNKPQJWPKEK", "c12fe1c06bba254a9dc9f519b335aa7c1367a88a", null)]
    public void Magnet_links_parse_hex_and_base32(string uri, string hash, string? name)
    {
        var (h, n) = Magnet.Parse(uri);
        Assert.Equal(hash, h);
        Assert.Equal(name, n);
    }

    private sealed class Progress(Action<TorrentTransferProgress> a) : IProgress<TorrentTransferProgress>
    {
        public void Report(TorrentTransferProgress value) { lock (this) a(value); }
    }
}

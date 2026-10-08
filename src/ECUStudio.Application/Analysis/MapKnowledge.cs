using System.Collections.Concurrent;
using System.Text.Json;
using ECUStudio.Calibration.Model;
using ECUStudio.Calibration.Scanning;
using ECUStudio.Core;

namespace ECUStudio.Application.Analysis;

/// <summary>
/// A map role confirmed by the user for one ECU software. It is reused only for files with the same plugin, SW and
/// HW number, and only where the structure at that address is identical (dimensions and raw axes).
/// </summary>
public sealed record MapConfirmation(string PluginId, string SoftwareNumber, string? HardwareNumber, int Address, string Structure, MapRole Role,
    string? Note, string SourceSha256, DateTimeOffset ConfirmedAt);

public interface IMapKnowledgeStore
{
    IReadOnlyList<MapConfirmation> All();
    void Save(MapConfirmation confirmation);
}

public sealed class InMemoryMapKnowledgeStore : IMapKnowledgeStore
{
    private readonly ConcurrentDictionary<(string, string, string?, int), MapConfirmation> _items = new();
    public IReadOnlyList<MapConfirmation> All() => _items.Values.ToList();
    public void Save(MapConfirmation c) => _items[(c.PluginId, c.SoftwareNumber, c.HardwareNumber, c.Address)] = c;
}

public sealed class JsonFileMapKnowledgeStore(string directory) : IMapKnowledgeStore
{
    private readonly object _lock = new();
    private string FilePath => Path.Combine(directory, "map-knowledge.json");

    public IReadOnlyList<MapConfirmation> All()
    {
        lock (_lock)
        {
            if (!File.Exists(FilePath)) return [];
            try { return JsonSerializer.Deserialize<List<MapConfirmation>>(File.ReadAllText(FilePath), Json.Options) ?? []; }
            catch (JsonException) { return []; }
        }
    }

    public void Save(MapConfirmation c)
    {
        lock (_lock)
        {
            var all = All().Where(x => !(x.PluginId == c.PluginId && x.SoftwareNumber == c.SoftwareNumber && x.HardwareNumber == c.HardwareNumber && x.Address == c.Address)).Append(c).ToList();
            Directory.CreateDirectory(directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json.Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}

public static class MapKnowledge
{
    /// <summary>Structure fingerprint: dimensions plus raw axis values.</summary>
    public static string Fingerprint(MapCandidate c) =>
        Hashing.Sha256Hex($"{c.Rows}x{c.Cols}|{string.Join(',', c.XAxisRaw)}|{string.Join(',', c.YAxisRaw)}")[..16];

    /// <summary>Confirmations that may apply to a file: same plugin, same SW and (when recorded) same HW.</summary>
    public static IEnumerable<MapConfirmation> Applicable(IEnumerable<MapConfirmation> all, string pluginId, string? sw, string? hw) =>
        sw is null ? [] : all.Where(c => c.PluginId == pluginId && c.SoftwareNumber == sw && (c.HardwareNumber is null || c.HardwareNumber == hw));
}

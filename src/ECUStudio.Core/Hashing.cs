using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ECUStudio.Core;

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    /// <summary>Hash of the canonical JSON form (declaration-ordered properties, shared options).</summary>
    public static string StableHash<T>(T value) => Sha256Hex(JsonSerializer.Serialize(value, Json.Options));

    public static string Combine(params string?[] parts) => Sha256Hex(string.Join('|', parts.Select(p => p ?? "-")));
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

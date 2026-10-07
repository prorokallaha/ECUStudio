using System.Text.Json;
using System.Text.Json.Nodes;

namespace ECUStudio.AI;

public sealed record AIRequest
{
    /// <summary>Stable system prompt (identical across agents → shared cache prefix).</summary>
    public required string SystemPrompt { get; init; }
    /// <summary>Stable structured context (identical across agents of one analysis → cached).</summary>
    public required string ContextJson { get; init; }
    /// <summary>Agent-specific instruction, placed after the cache breakpoint.</summary>
    public required string Instruction { get; init; }
    public required JsonObject OutputSchema { get; init; }
    public string Effort { get; init; } = "medium";
    public int MaxTokens { get; init; } = 16000;
    public string? Model { get; init; }
}

public sealed record AIUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static AIUsage Zero { get; } = new(0, 0, 0, 0);
    public AIUsage Add(AIUsage o) => new(InputTokens + o.InputTokens, OutputTokens + o.OutputTokens, CacheReadTokens + o.CacheReadTokens, CacheWriteTokens + o.CacheWriteTokens);
}

public sealed record AIResponse(JsonElement Json, string Model, AIUsage Usage, string? StopReason);

/// <summary>
/// Provider-neutral LLM abstraction. Business logic depends only on this interface,
/// so Claude can be swapped for another provider or a local model.
/// </summary>
public interface IAIProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<AIResponse> CompleteJsonAsync(AIRequest request, CancellationToken ct = default);
}

public sealed record AICacheEntry(string Key, string Agent, string Model, string Json, AIUsage Usage, DateTimeOffset CreatedAt);

public interface IAICacheStore
{
    Task<AICacheEntry?> GetAsync(string key, CancellationToken ct = default);
    Task PutAsync(AICacheEntry entry, CancellationToken ct = default);
}

public sealed class InMemoryAICacheStore : IAICacheStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AICacheEntry> _items = new();
    public Task<AICacheEntry?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_items.GetValueOrDefault(key));
    public Task PutAsync(AICacheEntry entry, CancellationToken ct = default) { _items[entry.Key] = entry; return Task.CompletedTask; }
    public int Count => _items.Count;
}

/// <summary>Provider used when no API key is configured: fails clearly instead of silently skipping.</summary>
public sealed class UnconfiguredAIProvider : IAIProvider
{
    public string Name => "none";
    public bool IsConfigured => false;
    public Task<AIResponse> CompleteJsonAsync(AIRequest request, CancellationToken ct = default) =>
        throw new Core.AIUnavailableException("No AI provider configured. Set ANTHROPIC_API_KEY to enable the Claude analysis layer.");
}

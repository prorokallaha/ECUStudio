using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using ECUStudio.Core;

namespace ECUStudio.AI;

public sealed record ClaudeOptions
{
    public string? ApiKey { get; init; } = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    public string Model { get; init; } = Environment.GetEnvironmentVariable("ECUSTUDIO_CLAUDE_MODEL") ?? "claude-opus-5-5";
}

/// <summary>
/// Claude via the official Anthropic C# SDK. Structured outputs (JSON schema) guarantee
/// parseable JSON; system prompt and context are marked for prompt caching so all analysts of one
/// analysis share the cached prefix.
/// </summary>
public sealed class ClaudeProvider : IAIProvider
{
    private readonly ClaudeOptions _options;
    private readonly AnthropicClient? _client;

    public ClaudeProvider(ClaudeOptions options)
    {
        _options = options;
        if (!string.IsNullOrWhiteSpace(options.ApiKey)) _client = new AnthropicClient { ApiKey = options.ApiKey };
    }

    public string Name => "claude";
    public bool IsConfigured => _client is not null;

    public async Task<AIResponse> CompleteJsonAsync(AIRequest request, CancellationToken ct = default)
    {
        if (_client is null) throw new AIUnavailableException("ANTHROPIC_API_KEY is not set");
        var schema = request.OutputSchema.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));

        var parameters = new MessageCreateParams
        {
            Model = request.Model ?? _options.Model,
            MaxTokens = request.MaxTokens,
            System = new List<TextBlockParam>
            {
                new() { Text = request.SystemPrompt, CacheControl = new CacheControlEphemeral() },
            },
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = new List<ContentBlockParam>
                    {
                        new TextBlockParam { Text = "<analysis_context>\n" + request.ContextJson + "\n</analysis_context>", CacheControl = new CacheControlEphemeral() },
                        new TextBlockParam { Text = request.Instruction },
                    },
                },
            ],
            OutputConfig = new OutputConfig
            {
                Effort = request.Effort switch { "low" => Effort.Low, "high" => Effort.High, "max" => Effort.Max, _ => Effort.Medium },
                Format = new JsonOutputFormat { Schema = schema },
            },
        };

        Message response;
        try
        {
            response = await _client.Messages.Create(parameters, ct);
        }
        catch (AnthropicRateLimitException ex) { throw new AIUnavailableException($"Claude rate limit: {ex.Message}"); }
        catch (Anthropic5xxException ex) { throw new AIUnavailableException($"Claude service error: {ex.Message}"); }
        catch (AnthropicBadRequestException ex) { throw new AIResponseException($"Claude rejected the request: {ex.Message}"); }
        catch (AnthropicApiException ex) { throw new AIUnavailableException($"Claude API error: {ex.Message}"); }

        var stop = response.StopReason?.ToString();
        if (stop is not null && stop.Contains("refusal", StringComparison.OrdinalIgnoreCase))
            throw new AIResponseException("Claude declined this request (refusal stop reason)");
        if (stop is not null && stop.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
            throw new AIResponseException("Claude response truncated (max_tokens)");

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        JsonElement json;
        try { json = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException ex) { throw new AIResponseException($"Claude returned invalid JSON: {ex.Message}"); }

        var u = response.Usage;
        var usage = new AIUsage(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0, u.CacheCreationInputTokens ?? 0);
        return new AIResponse(json, response.Model.ToString(), usage, stop);
    }
}

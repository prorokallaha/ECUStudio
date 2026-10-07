namespace ECUStudio.Core;

/// <summary>Progress of a long-running analysis step, streamed to clients via SSE.</summary>
public sealed record StepProgress(string Step, string Label, StepState State, double? Fraction = null, string? Message = null);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<StepState>))]
public enum StepState { Pending, Running, Done, Skipped, Failed }

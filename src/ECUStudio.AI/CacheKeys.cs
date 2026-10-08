using ECUStudio.Core;

namespace ECUStudio.AI;

public static class AICacheKeys
{
    /// <summary>
    /// binary_hash + stock_hash + vehicle_profile_hash + analysis_version, plus agent, context hash and model.
    /// The context hash makes a change in local results invalidate stale AI output.
    /// </summary>
    public static string Build(string binaryHash, string? stockHash, string profileHash, string analysisVersion, string agent, string contextHash, string model) =>
        Hashing.Combine(binaryHash, stockHash, profileHash, analysisVersion, agent, contextHash, model);
}

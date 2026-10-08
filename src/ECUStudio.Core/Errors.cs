namespace ECUStudio.Core;

/// <summary>Base for domain errors; the API maps them to a uniform error payload.</summary>
public class EcuStudioException(string code, string message, int httpStatus = 400, IReadOnlyDictionary<string, object?>? details = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int HttpStatus { get; } = httpStatus;
    public IReadOnlyDictionary<string, object?> Details { get; } = details ?? new Dictionary<string, object?>();
}

public sealed class InvalidBinaryException(string message) : EcuStudioException("INVALID_BINARY", message);
public sealed class UnsupportedEcuException(string message) : EcuStudioException("UNSUPPORTED_ECU", message, 422);
public sealed class DefinitionException(string message) : EcuStudioException("DEFINITION_ERROR", message, 422);
public sealed class IncompatibleBinariesException(string message) : EcuStudioException("INCOMPATIBLE_BINARIES", message, 422);
public sealed class InvalidVinException(string message) : EcuStudioException("INVALID_VIN", message);
public sealed class NotFoundException(string message) : EcuStudioException("NOT_FOUND", message, 404);
public sealed class AIUnavailableException(string message) : EcuStudioException("AI_UNAVAILABLE", message, 503);
public sealed class AIResponseException(string message) : EcuStudioException("AI_RESPONSE_INVALID", message, 502);

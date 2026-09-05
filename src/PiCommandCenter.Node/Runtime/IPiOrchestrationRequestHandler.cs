using System.Text.Json;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// Identity and event sink handed to <see cref="IPiOrchestrationRequestHandler"/> for one
/// worker request. <paramref name="EmitAsync"/> appends a normalized event to the durable
/// session stream with correct sequencing; it is the only persistence path.
/// </summary>
public sealed record PiOrchestrationContext(
    string SessionId,
    string NodeId,
    string ProjectId,
    string RequestId,
    string? ParentSessionId,
    Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task> EmitAsync,
    string? RepositoryRoot = null);

/// <summary>Structured answer returned to the worker for one custom-tool request.</summary>
public sealed record PiToolResponse
{
    private PiToolResponse(bool ok, object? result, string? errorCode, string? errorMessage)
    {
        Ok = ok;
        Result = result;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public bool Ok { get; }

    public object? Result { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public static PiToolResponse Success(object? result = null) => new(true, result, null, null);

    public static PiToolResponse Failure(string errorCode, string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        return new PiToolResponse(false, null, errorCode, errorMessage);
    }
}

/// <summary>
/// Handles custom-tool requests arriving from the Pi worker (worker→Node <c>request</c> frames).
/// Implementations persist observable state themselves and must never fake success.
/// </summary>
public interface IPiOrchestrationRequestHandler
{
    Task<PiToolResponse> HandleAsync(
        PiOrchestrationContext context,
        string requestType,
        JsonElement? payload,
        CancellationToken cancellationToken);
}

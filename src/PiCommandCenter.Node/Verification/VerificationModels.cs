using PiCommandCenter.Application.Verification;

namespace PiCommandCenter.Node.Verification;

/// <summary>Identity of one verification invocation against a canonical repository.</summary>
public sealed record VerificationRunContext(
    Guid ProjectId,
    Guid RequestId,
    string OwnerSessionId,
    string RepositoryRoot,
    Func<VerificationCommandStarting, CancellationToken, Task>? OnCommandStarting = null);

/// <summary>Captured result of one trusted configured command.</summary>
public sealed record VerificationCommandResult(
    string CommandId,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int? ExitCode,
    TimeSpan Duration,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    bool Crashed,
    bool OutputTruncated,
    string? ArtifactPath,
    bool Mandatory);

/// <summary>
/// Bounded command-progress fact. Never carries executable, argv, environment, or process output.
/// </summary>
public sealed record VerificationCommandStarting(
    string CommandId,
    bool Mandatory,
    int TimeoutSeconds);

/// <summary>Aggregated result of a profile (or a single command within it).</summary>
public sealed record VerificationProfileRunResult(
    string ProfileId,
    IReadOnlyList<VerificationCommandResult> Commands,
    bool Succeeded);
/// <summary>Immutable verification policy captured for an execution assignment.</summary>
public sealed record RequestVerificationPolicy(
    string Revision,
    string BaselineVersion,
    string? TrustedProfileId,
    string? TrustedProfileRevision,
    IReadOnlyList<string> MandatoryCommandIds);

/// <summary>
/// Node-owned inputs and persistence/event sinks for semantic request verification.
/// No executable, profile selector, or command selector comes from agent content.
/// </summary>
public sealed record RequestVerificationContext(
    Guid ProjectId,
    Guid RequestId,
    Guid? WorkspaceBindingId,
    long? BindingValidationRevision,
    string RequestingSessionId,
    string RepositoryRoot,
    string BaselineCommit,
    string BaselineBranch,
    RequestVerificationPolicy? Policy,
    IReadOnlyList<VerificationRunDto> ExistingRuns,
    Func<string, IReadOnlyDictionary<string, object?>, CancellationToken, Task> EmitAsync,
    Func<VerificationRunDto, CancellationToken, Task> PersistRunAsync);

/// <summary>Semantic outcome of a final or intermediate verification request.</summary>
public enum RequestVerificationDecisionKind
{
    Passed = 0,
    Failed = 1,
    Rejected = 2,
    Cancelled = 3,
    Reused = 4,
}

/// <summary>Bounded result returned to orchestration callers.</summary>
public sealed record RequestVerificationDecision(
    RequestVerificationDecisionKind Kind,
    string Summary,
    string? Fingerprint = null,
    string? PolicyRevision = null,
    string? ErrorCode = null)
{
    public bool IsGreen =>
        Kind is RequestVerificationDecisionKind.Passed or RequestVerificationDecisionKind.Reused;
}

/// <summary>
/// Owns semantic final and intermediate verification. Callers cannot select profiles or commands.
/// </summary>
public interface IRequestVerificationCoordinator
{
    Task<RequestVerificationDecision> VerifyFinalAsync(
        RequestVerificationContext context,
        CancellationToken cancellationToken);

    Task<RequestVerificationDecision> VerifyIntermediateAsync(
        RequestVerificationContext context,
        CancellationToken cancellationToken);

    Task<string> CaptureFingerprintAsync(
        RequestVerificationContext context,
        CancellationToken cancellationToken);
}


/// <summary>Raised when verification cannot start because of reservations or unknown profiles.</summary>
public sealed class VerificationRejectedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>Runs trusted verification profiles. Callers supply a profile id, never an executable.</summary>
public interface IVerificationCommandRunner
{
    Task<VerificationProfileRunResult> RunAsync(
        VerificationRunContext context,
        string profileId,
        string? commandId,
        CancellationToken cancellationToken);
}

/// <summary>Coordinator-facing runner used only after admission and the build lease are held.</summary>
public interface IAdmittedVerificationCommandRunner
{
    Task<VerificationProfileRunResult> RunAdmittedAsync(
        VerificationRunContext context,
        string profileId,
        CancellationToken cancellationToken);
}

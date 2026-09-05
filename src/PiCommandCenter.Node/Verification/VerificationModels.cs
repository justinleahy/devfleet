namespace PiCommandCenter.Node.Verification;

/// <summary>Identity of one verification invocation against a canonical repository.</summary>
public sealed record VerificationRunContext(
    Guid ProjectId,
    Guid RequestId,
    string OwnerSessionId,
    string RepositoryRoot);

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

/// <summary>Aggregated result of a profile (or a single command within it).</summary>
public sealed record VerificationProfileRunResult(
    string ProfileId,
    IReadOnlyList<VerificationCommandResult> Commands,
    bool Succeeded);

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

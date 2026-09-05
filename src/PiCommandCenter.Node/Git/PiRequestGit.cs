namespace PiCommandCenter.Node.Git;

/// <summary>Canonical request-branch derivation shared by supervisor and adapter.</summary>
public static class PiRequestGit
{
    /// <summary>The deterministic request branch name for a work request id.</summary>
    public static string RequestBranchName(Guid requestId) => $"request/{requestId:N}";
}

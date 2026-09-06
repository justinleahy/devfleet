using System.Diagnostics.CodeAnalysis;

namespace PiCommandCenter.Node;

/// <summary>
/// The opaque claim credential for one assignment active on this node.
/// </summary>
public sealed record NodeAssignmentCredential
{
    internal const int MaxClaimTokenLength = 128;

    public NodeAssignmentCredential(Guid requestId, Guid projectId, string claimToken)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("Request id is required.", nameof(requestId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        if (claimToken.Length > MaxClaimTokenLength)
        {
            throw new ArgumentException(
                $"Claim token must not exceed {MaxClaimTokenLength} characters.",
                nameof(claimToken));
        }

        RequestId = requestId;
        ProjectId = projectId;
        ClaimToken = claimToken;
    }

    public Guid RequestId { get; }

    public Guid ProjectId { get; }

    public string ClaimToken { get; }

    public override string ToString() => nameof(NodeAssignmentCredential);
}

/// <summary>
/// Resolves credentials for assignments active on this node.
/// </summary>
public interface INodeAssignmentCredentialSource
{
    bool TryGetByRequest(
        Guid requestId,
        [NotNullWhen(true)] out NodeAssignmentCredential? credential);

    bool TryGetByProject(
        Guid projectId,
        [NotNullWhen(true)] out NodeAssignmentCredential? credential);
}

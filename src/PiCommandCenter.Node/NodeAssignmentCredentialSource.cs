using System.Diagnostics.CodeAnalysis;

namespace PiCommandCenter.Node;

/// <summary>
/// Thread-safe in-memory projection of credentials for assignments active on this node.
/// </summary>
public sealed class NodeAssignmentCredentialSource : INodeAssignmentCredentialSource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, NodeAssignmentCredential> _credentialsByRequest = new();
    private readonly Dictionary<Guid, NodeAssignmentCredential> _credentialsByProject = new();

    public bool TryGetByRequest(
        Guid requestId,
        [NotNullWhen(true)] out NodeAssignmentCredential? credential)
    {
        lock (_gate)
        {
            return _credentialsByRequest.TryGetValue(requestId, out credential);
        }
    }

    public bool TryGetByProject(
        Guid projectId,
        [NotNullWhen(true)] out NodeAssignmentCredential? credential)
    {
        lock (_gate)
        {
            return _credentialsByProject.TryGetValue(projectId, out credential);
        }
    }

    internal void Track(NodeAssignmentCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        lock (_gate)
        {
            if (_credentialsByRequest.TryGetValue(credential.RequestId, out var requestCredential))
            {
                RemoveExact(requestCredential);
            }

            if (_credentialsByProject.TryGetValue(credential.ProjectId, out var projectCredential))
            {
                RemoveExact(projectCredential);
            }

            _credentialsByRequest[credential.RequestId] = credential;
            _credentialsByProject[credential.ProjectId] = credential;
        }
    }

    internal void Remove(NodeAssignmentCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        lock (_gate)
        {
            RemoveExact(credential);
        }
    }

    private void RemoveExact(NodeAssignmentCredential credential)
    {
        if (!_credentialsByRequest.TryGetValue(credential.RequestId, out var current)
            || current != credential)
        {
            return;
        }

        _credentialsByRequest.Remove(credential.RequestId);
        _credentialsByProject.Remove(credential.ProjectId);
    }
}

using PiCommandCenter.Node.Quiescence;

namespace PiCommandCenter.Node.Child;

/// <summary>Outcome of one reserved filesystem operation attempt.</summary>
public record FileOperationResult(bool Ok, string? ErrorCode, string? ErrorMessage)
{
    public static FileOperationResult Success() => new(true, null, null);

    public static FileOperationResult Failure(string code, string message) => new(false, code, message);
}

/// <summary>
/// Lease + fencing-token data carried by every child mutation request. Authorization happens
/// immediately before the mutation touches the filesystem; a denied or stale decision leaves
/// the filesystem untouched.
/// </summary>
public sealed record MutationLease(Guid LeaseId, long FencingToken);

/// <summary>
/// Reservation-authorized filesystem operations for Pi children (SPEC §18.1). Every operation
/// first resolves both paths through <see cref="RepositoryPathPolicy"/> (symlink-escape safe),
/// then asks the reservation authority to authorize the lease/fencing token for the exact
/// target path and operation, and only then mutates. A <c>reserved_move</c> authorizes the
/// source and the destination separately before either changes.
/// </summary>
public sealed class ReservedFileOperations
{
    private readonly INodeReservationGateway _reservations;
    private readonly IRequestAdmissionGate _admission;

    public ReservedFileOperations(INodeReservationGateway reservations, IRequestAdmissionGate admission)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(admission);
        _reservations = reservations;
        _admission = admission;
    }

    private NodeActivityLease? EnterMutation(Guid requestId, string operation)
        => _admission.TryEnterOperation(requestId, operation);

    private static FileOperationResult AdmissionClosed()
        => FileOperationResult.Failure(
            "admission_closed",
            "The request is terminalizing; no new mutation work is admitted.");

    public async Task<FileOperationResult> ReadTextAsync(
        string repositoryRoot,
        MutationLease lease,
        string sessionId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        string path;
        try
        {
            path = RepositoryPathPolicy.Resolve(repositoryRoot, relativePath);
        }
        catch (RepositoryPathPolicyException ex)
        {
            return FileOperationResult.Failure(ex.Code, ex.Message);
        }

        var authorization = await AuthorizeAsync(
            lease, sessionId, relativePath, "read", cancellationToken).ConfigureAwait(false);
        if (!authorization.Authorized)
        {
            return Denied(authorization);
        }

        try
        {
            return new ReadResult(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return FileOperationResult.Failure("read_failed", ex.Message);
        }
    }

    public async Task<FileOperationResult> WriteTextAsync(
        Guid requestId,
        string repositoryRoot,
        MutationLease lease,
        string sessionId,
        string relativePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        using var activity = EnterMutation(requestId, "reserved_write");
        if (activity is null)
        {
            return AdmissionClosed();
        }

        string path;
        try
        {
            path = RepositoryPathPolicy.Resolve(repositoryRoot, relativePath);
        }
        catch (RepositoryPathPolicyException ex)
        {
            return FileOperationResult.Failure(ex.Code, ex.Message);
        }

        var authorization = await AuthorizeAsync(
            lease, sessionId, relativePath, "write", cancellationToken).ConfigureAwait(false);
        if (!authorization.Authorized)
        {
            return Denied(authorization);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return FileOperationResult.Success();
        }
        catch (Exception ex)
        {
            return FileOperationResult.Failure("write_failed", ex.Message);
        }
    }

    public async Task<FileOperationResult> EditTextAsync(
        Guid requestId,
        string repositoryRoot,
        MutationLease lease,
        string sessionId,
        string relativePath,
        string oldText,
        string newText,
        CancellationToken cancellationToken = default)
    {
        using var activity = EnterMutation(requestId, "reserved_edit");
        if (activity is null)
        {
            return AdmissionClosed();
        }

        string path;
        try
        {
            path = RepositoryPathPolicy.Resolve(repositoryRoot, relativePath);
        }
        catch (RepositoryPathPolicyException ex)
        {
            return FileOperationResult.Failure(ex.Code, ex.Message);
        }

        var authorization = await AuthorizeAsync(
            lease, sessionId, relativePath, "edit", cancellationToken).ConfigureAwait(false);
        if (!authorization.Authorized)
        {
            return Denied(authorization);
        }

        try
        {
            var text = File.ReadAllText(path);
            if (!text.Contains(oldText, StringComparison.Ordinal))
            {
                return FileOperationResult.Failure(
                    "edit_target_not_found", "The search text was not found; the file is unchanged.");
            }

            File.WriteAllText(path, text.Replace(oldText, newText, StringComparison.Ordinal));
            return FileOperationResult.Success();
        }
        catch (Exception ex)
        {
            return FileOperationResult.Failure("edit_failed", ex.Message);
        }
    }

    public async Task<FileOperationResult> DeleteAsync(
        Guid requestId,
        string repositoryRoot,
        MutationLease lease,
        string sessionId,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        using var activity = EnterMutation(requestId, "reserved_delete");
        if (activity is null)
        {
            return AdmissionClosed();
        }

        string path;
        try
        {
            path = RepositoryPathPolicy.Resolve(repositoryRoot, relativePath);
        }
        catch (RepositoryPathPolicyException ex)
        {
            return FileOperationResult.Failure(ex.Code, ex.Message);
        }

        var authorization = await AuthorizeAsync(
            lease, sessionId, relativePath, "delete", cancellationToken).ConfigureAwait(false);
        if (!authorization.Authorized)
        {
            return Denied(authorization);
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }

            return FileOperationResult.Success();
        }
        catch (Exception ex)
        {
            return FileOperationResult.Failure("delete_failed", ex.Message);
        }
    }

    /// <summary>
    /// Moves a file or directory between two repository-relative paths. Both the source and the
    /// destination are authorized before anything changes, so a lease covering only one side
    /// of the move can never mutate the repository.
    /// </summary>
    public async Task<FileOperationResult> MoveAsync(
        Guid requestId,
        string repositoryRoot,
        MutationLease lease,
        string sessionId,
        string sourceRelativePath,
        string destinationRelativePath,
        CancellationToken cancellationToken = default)
    {
        using var activity = EnterMutation(requestId, "reserved_move");
        if (activity is null)
        {
            return AdmissionClosed();
        }

        string source;
        string destination;
        try
        {
            source = RepositoryPathPolicy.Resolve(repositoryRoot, sourceRelativePath);
            destination = RepositoryPathPolicy.Resolve(repositoryRoot, destinationRelativePath);
        }
        catch (RepositoryPathPolicyException ex)
        {
            return FileOperationResult.Failure(ex.Code, ex.Message);
        }

        var sourceAuthorization = await AuthorizeAsync(
            lease, sessionId, sourceRelativePath, "move", cancellationToken).ConfigureAwait(false);
        if (!sourceAuthorization.Authorized)
        {
            return Denied(sourceAuthorization);
        }

        var destinationAuthorization = await AuthorizeAsync(
            lease, sessionId, destinationRelativePath, "move", cancellationToken).ConfigureAwait(false);
        if (!destinationAuthorization.Authorized)
        {
            return Denied(destinationAuthorization);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (Directory.Exists(source))
            {
                Directory.Move(source, destination);
            }
            else
            {
                File.Move(source, destination);
            }

            return FileOperationResult.Success();
        }
        catch (Exception ex)
        {
            return FileOperationResult.Failure("move_failed", ex.Message);
        }
    }

    private Task<MutationAuthorizationResult> AuthorizeAsync(
        MutationLease lease,
        string sessionId,
        string relativePath,
        string operation,
        CancellationToken cancellationToken = default)
        => _reservations.AuthorizeAsync(
            lease.LeaseId, lease.FencingToken, sessionId, relativePath, operation, cancellationToken);

    private static FileOperationResult Denied(MutationAuthorizationResult authorization)
        => FileOperationResult.Failure(
            authorization.Error?.Code ?? "mutation_denied",
            authorization.Error?.Message ?? "The reservation authority denied the mutation; the filesystem is unchanged.");

    /// <summary>Extracts the content from a successful read result.</summary>
    public static string ReadContent(FileOperationResult result)
        => result is ReadResult read
            ? read.Content
            : throw new InvalidOperationException("The read operation did not produce content.");
}

/// <summary>Successful read outcome carrying the file content.</summary>
public sealed record ReadResult(string Content) : FileOperationResult(true, null, null);

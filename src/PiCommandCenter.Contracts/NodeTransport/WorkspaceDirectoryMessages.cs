namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>SignalR callback name for node-local workspace directory browsing.</summary>
public static class WorkspaceDirectoryBrowseCallback
{
    public const string MethodName = "BrowseWorkspaceDirectories";
}

/// <summary>Stable error codes for node-local workspace directory browsing.</summary>
public static class WorkspaceDirectoryBrowseErrorCodes
{
    public const string InvalidPath = "invalid_path";
    public const string PathMissing = "path_missing";
    public const string OutsideApprovedRoot = "outside_approved_root";
    public const string Unreadable = "unreadable";
}

/// <summary>
/// Node-local directory listing request. A null <see cref="Path"/> lists the node's
/// configured approved roots. A non-null path lists only existing direct child directories
/// inside an approved root; results are bounded and never leave that tree.
/// </summary>
public sealed record WorkspaceDirectoryBrowseRequestMessage(string? Path);

/// <summary>One existing directory returned from a node-local browse.</summary>
public sealed record WorkspaceDirectoryEntryMessage(string Name, string Path);

/// <summary>
/// Node-local directory listing result. Root-list responses (null request path) have null
/// <see cref="CurrentPath"/> and <see cref="ParentPath"/>. A successful directory response
/// uses the canonical absolute <see cref="CurrentPath"/>, a null <see cref="ParentPath"/>
/// when that path is an approved root, and at most 500 sorted direct child directories.
/// Errors return no entries, a stable <see cref="ErrorCode"/>, and operator-safe
/// <see cref="ErrorDetail"/> of at most 512 characters.
/// </summary>
public sealed record WorkspaceDirectoryBrowseResponseMessage(
    string? CurrentPath,
    string? ParentPath,
    IReadOnlyList<WorkspaceDirectoryEntryMessage> Directories,
    string? ErrorCode,
    string? ErrorDetail);

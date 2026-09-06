using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Git;
using PiCommandCenter.Node.Repository;

namespace PiCommandCenter.Node.Verification;

public sealed record BaselineVerificationContext(
    Guid RequestId,
    Guid WorkspaceBindingId,
    long BindingValidationRevision,
    string RepositoryRoot,
    string BaselineCommit,
    string CurrentBranchOrHead,
    string PolicyRevision,
    Func<VerificationCommandStarting, CancellationToken, Task>? OnCommandStarting = null);

public sealed record BaselineVerificationResult(
    string Fingerprint,
    VerificationCommandResult RepositoryIntegrity,
    VerificationCommandResult Whitespace)
{
    public IReadOnlyList<VerificationCommandResult> Commands =>
        [RepositoryIntegrity, Whitespace];

    public bool Succeeded =>
        !RepositoryIntegrity.TimedOut
        && !RepositoryIntegrity.Cancelled
        && !RepositoryIntegrity.Crashed
        && RepositoryIntegrity.ExitCode == 0;
}

public interface IBaselineVerification
{
    const string ProfileId = VerificationBaselineIds.ProfileId;
    const string Version = VerificationBaselineIds.Version;
    const string RepositoryIntegrityCommandId = VerificationBaselineIds.RepositoryIntegrityCommandId;
    const string WhitespaceCommandId = VerificationBaselineIds.WhitespaceCommandId;

    Task<string> CaptureFingerprintAsync(
        BaselineVerificationContext context,
        CancellationToken cancellationToken);

    Task<BaselineVerificationResult> RunAsync(
        BaselineVerificationContext context,
        string fingerprint,
        CancellationToken cancellationToken);
}

/// <summary>
/// Performs the built-in repository baseline without loading or executing repository code.
/// All Git invocations use supervisor-owned fixed argument vectors and an isolated process sandbox.
/// </summary>
public sealed class BaselineVerification : IBaselineVerification
{
    public const int DefaultTimeoutSeconds = 900;

    private const int MaxOutputBytes = 64 * 1024;
    private readonly TimeSpan _deadline;

    public BaselineVerification()
        : this(TimeSpan.FromSeconds(DefaultTimeoutSeconds))
    {
    }

    public BaselineVerification(TimeSpan deadline)
    {
        _deadline = deadline > TimeSpan.Zero ? deadline : TimeSpan.FromTicks(1);
    }

    public async Task<string> CaptureFingerprintAsync(
        BaselineVerificationContext context,
        CancellationToken cancellationToken)
    {
        _ = ValidateContext(context);
        using var deadline = CreateDeadline(cancellationToken);
        try
        {
            var snapshot = await InspectAsync(context, deadline.Token).ConfigureAwait(false);
            return await ComputeFingerprintAsync(context, snapshot, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ComputeUnavailableFingerprint(context, nameof(TimeoutException));
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or TimeoutException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return ComputeUnavailableFingerprint(context, ex.GetType().Name);
        }
    }

    public async Task<BaselineVerificationResult> RunAsync(
        BaselineVerificationContext context,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        await NotifyCommandStartingAsync(
            context,
            IBaselineVerification.RepositoryIntegrityCommandId,
            mandatory: true,
            cancellationToken).ConfigureAwait(false);

        var integrityTimer = Stopwatch.StartNew();
        RepositorySnapshot? snapshot = null;
        VerificationCommandResult integrity;
        using var integrityDeadline = CreateDeadline(cancellationToken);
        try
        {
            snapshot = await InspectAsync(context, integrityDeadline.Token).ConfigureAwait(false);
            var observedFingerprint = await ComputeFingerprintAsync(
                    context,
                    snapshot,
                    integrityDeadline.Token)
                .ConfigureAwait(false);
            var errors = new List<string>();
            if (!string.Equals(observedFingerprint, fingerprint, StringComparison.Ordinal))
            {
                errors.Add("Repository content changed after its verification fingerprint was captured.");
            }

            if (!string.IsNullOrEmpty(snapshot.UnmergedIndex))
            {
                errors.Add("The Git index contains unmerged entries.");
            }

            if (snapshot.GitlinkPaths.Count > 0)
            {
                errors.Add("The Git index contains unsupported gitlink entries.");
            }

            integrity = CommandResult(
                IBaselineVerification.RepositoryIntegrityCommandId,
                ["rev-parse", "--show-toplevel"],
                context.RepositoryRoot,
                errors.Count == 0 ? 0 : 1,
                integrityTimer.Elapsed,
                errors.Count == 0 ? "Repository integrity passed." : string.Empty,
                string.Join(Environment.NewLine, errors),
                mandatory: true);
        }
        catch (OperationCanceledException ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                integrity = CommandResult(
                    IBaselineVerification.RepositoryIntegrityCommandId,
                    ["rev-parse", "--show-toplevel"],
                    context.RepositoryRoot,
                    exitCode: null,
                    integrityTimer.Elapsed,
                    string.Empty,
                    "Repository integrity was cancelled.",
                    mandatory: true,
                    cancelled: true);
            }
            else
            {
                integrity = CommandResult(
                    IBaselineVerification.RepositoryIntegrityCommandId,
                    ["rev-parse", "--show-toplevel"],
                    context.RepositoryRoot,
                    exitCode: null,
                    integrityTimer.Elapsed,
                    string.Empty,
                    "Repository integrity timed out.",
                    mandatory: true,
                    timedOut: true);
                _ = ex;
            }
        }
        catch (TimeoutException ex)
        {
            integrity = CommandResult(
                IBaselineVerification.RepositoryIntegrityCommandId,
                ["rev-parse", "--show-toplevel"],
                context.RepositoryRoot,
                exitCode: null,
                integrityTimer.Elapsed,
                string.Empty,
                ex.Message,
                mandatory: true,
                timedOut: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            integrity = CommandResult(
                IBaselineVerification.RepositoryIntegrityCommandId,
                ["rev-parse", "--show-toplevel"],
                context.RepositoryRoot,
                exitCode: null,
                integrityTimer.Elapsed,
                string.Empty,
                ex.Message,
                mandatory: true,
                crashed: true);
        }

        VerificationCommandResult whitespace;
        if (snapshot is null || integrity.ExitCode != 0)
        {
            whitespace = CommandResult(
                IBaselineVerification.WhitespaceCommandId,
                WhitespaceArguments(context.BaselineCommit),
                context.RepositoryRoot,
                exitCode: null,
                TimeSpan.Zero,
                string.Empty,
                "Whitespace was not checked because repository integrity did not pass.",
                mandatory: false,
                crashed: true);
        }
        else
        {
            await NotifyCommandStartingAsync(
                context,
                IBaselineVerification.WhitespaceCommandId,
                mandatory: false,
                cancellationToken).ConfigureAwait(false);
            using var whitespaceDeadline = CreateDeadline(cancellationToken);
            whitespace = await CheckWhitespaceAsync(
                    context,
                    snapshot.UntrackedPaths,
                    whitespaceDeadline.Token)
                .ConfigureAwait(false);
            if (whitespace.Cancelled && !cancellationToken.IsCancellationRequested)
            {
                whitespace = whitespace with
                {
                    TimedOut = true,
                    Cancelled = false,
                    StandardError = "Whitespace inspection timed out.",
                };
            }
        }

        return new BaselineVerificationResult(fingerprint, integrity, whitespace);
    }

    private static async Task<RepositorySnapshot> InspectAsync(
        BaselineVerificationContext context,
        CancellationToken cancellationToken)
    {
        var root = ValidateContext(context);
        var safety = await RepositoryInspector.ReadRepositoryGitSafetyConfigAsync(
            root,
            cancellationToken).ConfigureAwait(false);
        Task<string> ReadGitAsync(IReadOnlyList<string> arguments) =>
            RepositoryInspector.RunGitReadOnlyAsync(
                root,
                arguments,
                safety.FilterDrivers,
                cancellationToken,
                safety.CoreWhitespace);

        var topLevel = (await ReadGitAsync(["rev-parse", "--show-toplevel"]).ConfigureAwait(false)).Trim();
        if (!SamePath(root, topLevel))
        {
            throw new InvalidOperationException(
                "Assigned workspace is not the inspected Git root.");
        }

        var resolvedBaseline = (await ReadGitAsync(
            ["rev-parse", "--verify", $"{context.BaselineCommit}^{{commit}}"])
            .ConfigureAwait(false)).Trim();
        var branch = (await ReadGitAsync(["rev-parse", "--abbrev-ref", "HEAD"])
            .ConfigureAwait(false)).Trim();
        var head = (await ReadGitAsync(["rev-parse", "HEAD"]).ConfigureAwait(false)).Trim();

        var requestBranch = PiRequestGit.RequestBranchName(context.RequestId);
        if (!string.Equals(context.CurrentBranchOrHead, branch, StringComparison.Ordinal)
            && !string.Equals(context.CurrentBranchOrHead, head, StringComparison.Ordinal)
            && !string.Equals(requestBranch, branch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Repository is not at the expected branch or head.");
        }

        var unmerged = await ReadGitAsync(["ls-files", "-u", "-z"]).ConfigureAwait(false);
        var indexEntries = RepositoryInspector.CanonicalizeNullSeparatedRecords(
            await ReadGitAsync(["ls-files", "--stage", "-z"]).ConfigureAwait(false));
        var gitlinkPaths = indexEntries
            .Where(static entry => entry.StartsWith("160000 ", StringComparison.Ordinal))
            .Select(static entry =>
            {
                var pathSeparator = entry.IndexOf('\t');
                return pathSeparator >= 0
                    ? entry[(pathSeparator + 1)..]
                    : throw new InvalidOperationException("Git returned a malformed index entry.");
            })
            .Select(path => RepositoryInspector.EnsureSafeRepositoryPath(root, path))
            .ToHashSet(StringComparer.Ordinal);
        var tracked = ParsePaths(
            await ReadGitAsync(["ls-files", "--cached", "-z"]).ConfigureAwait(false),
            root);
        var untracked = ParsePaths(
            await ReadGitAsync(["ls-files", "--others", "--exclude-standard", "-z"])
                .ConfigureAwait(false),
            root);

        return new RepositorySnapshot(
            root,
            resolvedBaseline,
            branch,
            head,
            indexEntries,
            unmerged,
            gitlinkPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            tracked.Where(path => !gitlinkPaths.Contains(path))
                .Concat(untracked)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            untracked);
    }

    private static async Task<string> ComputeFingerprintAsync(
        BaselineVerificationContext context,
        RepositorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var files = new List<FileIdentity>(snapshot.ContentPaths.Count);
        foreach (var path in snapshot.ContentPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(await ReadFileIdentityAsync(snapshot.Root, path, cancellationToken).ConfigureAwait(false));
        }
        files.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", context.RequestId);
            writer.WriteString("workspaceBindingId", context.WorkspaceBindingId);
            writer.WriteNumber("bindingValidationRevision", context.BindingValidationRevision);
            writer.WriteString("repositoryRoot", snapshot.Root);
            writer.WriteString("baselineCommit", snapshot.BaselineCommit);
            writer.WriteString("currentBranchOrHead", context.CurrentBranchOrHead);
            writer.WriteString("head", snapshot.Head);
            writer.WriteStartArray("index");
            foreach (var entry in snapshot.IndexEntries)
            {
                writer.WriteStringValue(entry);
            }
            writer.WriteEndArray();
            writer.WriteString("policyRevision", context.PolicyRevision);
            writer.WriteStartArray("files");
            foreach (var file in files)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(file.Path);
                writer.WriteStringValue(file.Kind);
                writer.WriteStringValue(file.ContentHash);
                writer.WriteNumberValue(file.GitFileMode);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static async Task<FileIdentity> ReadFileIdentityAsync(
        string root,
        string path,
        CancellationToken cancellationToken)
    {
        var safePath = RepositoryInspector.EnsureSafeRepositoryPath(root, path);
        var fullPath = Path.GetFullPath(Path.Combine(root, safePath));
        try
        {
            using var handle = OpenContentHandle(fullPath);
            EnsureOpenedFileMatchesPath(root, fullPath, handle);
            if (OperatingSystem.IsLinux())
            {
                var fileType = GetLinuxFileType(handle);
                if (fileType == Native.DirectoryFile)
                {
                    return new FileIdentity(safePath, "directory", string.Empty, 0);
                }
                if (fileType != Native.RegularFile)
                {
                    throw new InvalidOperationException(
                        "Repository content path is not a regular file.");
                }
            }

            var length = RandomAccess.GetLength(handle);
            return new FileIdentity(
                safePath,
                "file",
                await HashFileAsync(handle, length, cancellationToken).ConfigureAwait(false),
                GitFileMode(handle));
        }
        catch (FileNotFoundException)
        {
            return new FileIdentity(safePath, "missing", string.Empty, 0);
        }
        catch (DirectoryNotFoundException)
        {
            return new FileIdentity(safePath, "missing", string.Empty, 0);
        }
        catch (UnauthorizedAccessException) when (Directory.Exists(fullPath))
        {
            return new FileIdentity(safePath, "directory", string.Empty, 0);
        }
    }

    private static SafeFileHandle OpenContentHandle(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.Asynchronous);
        }

        var descriptor = Native.open(path, Native.OpenContentReadFlags, 0);
        if (descriptor >= 0)
        {
            return new SafeFileHandle(descriptor, ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        return error switch
        {
            Native.Enoent => throw new FileNotFoundException("Repository content path was not found.", path),
            Native.Enotdir => throw new DirectoryNotFoundException(
                "A repository content path component was not found."),
            Native.Enxio => throw new InvalidOperationException(
                "Repository content path is not a regular file."),
            _ => throw new IOException($"Repository content path could not be opened (errno {error})."),
        };
    }

    private static ushort GetLinuxMode(SafeFileHandle handle)
    {
        var status = new byte[Native.StatxSize];
        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (Native.statx(
                descriptor,
                string.Empty,
                Native.AtEmptyPath,
                Native.StatxBasicStats,
                status) != 0)
        {
            throw new IOException("Repository content file type could not be verified.");
        }

        return MemoryMarshal.Read<ushort>(status.AsSpan(28));
    }

    private static ushort GetLinuxFileType(SafeFileHandle handle) =>
        (ushort)(GetLinuxMode(handle) & Native.FileTypeMask);

    private static int GitFileMode(SafeFileHandle handle)
    {
        const int gitFile = 0b1000000110100100;
        const int gitExecutable = 0b1000000111101101;
        if (OperatingSystem.IsLinux())
        {
            return (GetLinuxMode(handle) & 0x49) == 0 ? gitFile : gitExecutable;
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var unix = File.GetUnixFileMode(handle);
                return (unix & UnixFileMode.UserExecute) == 0 ? gitFile : gitExecutable;
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        return gitFile;
    }

    private static async Task<string> HashFileAsync(
        SafeFileHandle handle,
        long length,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 64 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                var requested = (int)Math.Min(bufferSize, length - offset);
                var read = await RandomAccess.ReadAsync(
                    handle,
                    buffer.AsMemory(0, requested),
                    offset,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("Repository content changed while it was inspected.");
                }

                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            if (await RandomAccess.ReadAsync(
                    handle,
                    buffer.AsMemory(0, 1),
                    offset,
                    cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new IOException("Repository content changed while it was inspected.");
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureOpenedFileMatchesPath(
        string root,
        string fullPath,
        SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var descriptor = handle.DangerousGetHandle().ToInt64();
        var descriptorPath = $"/proc/self/fd/{descriptor}";
        var resolved = File.ResolveLinkTarget(descriptorPath, returnFinalTarget: true)
            ?? throw new InvalidOperationException("Opened repository file identity could not be verified.");
        var resolvedPath = Path.GetFullPath(resolved.FullName);
        if (!SamePath(fullPath, resolvedPath)
            || !resolvedPath.StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Opened repository file does not match its verified workspace path.");
        }
    }

    private static string ComputeUnavailableFingerprint(
        BaselineVerificationContext context,
        string failureKind)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", context.RequestId);
            writer.WriteString("workspaceBindingId", context.WorkspaceBindingId);
            writer.WriteNumber("bindingValidationRevision", context.BindingValidationRevision);
            writer.WriteString("repositoryRoot", Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryRoot)));
            writer.WriteString("baselineCommit", context.BaselineCommit);
            writer.WriteString("currentBranchOrHead", context.CurrentBranchOrHead);
            writer.WriteString("policyRevision", context.PolicyRevision);
            writer.WriteString("inspectionFailure", failureKind);
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static async Task<VerificationCommandResult> CheckWhitespaceAsync(
        BaselineVerificationContext context,
        IReadOnlyList<string> untrackedPaths,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var output = new StringBuilder();
        var errors = new StringBuilder();
        var outputTruncated = false;
        var timedOut = false;
        var cancelled = false;
        var crashed = false;
        var hasWhitespaceErrors = false;

        try
        {
            var safety = await RepositoryInspector.ReadRepositoryGitSafetyConfigAsync(
                context.RepositoryRoot,
                cancellationToken).ConfigureAwait(false);
            var tracked = await RepositoryInspector.RunGitReadOnlyCheckAsync(
                context.RepositoryRoot,
                WhitespaceArguments(context.BaselineCommit),
                safety.FilterDrivers,
                cancellationToken,
                safety.CoreWhitespace).ConfigureAwait(false);
            Accumulate(tracked.StandardOutput, output, ref outputTruncated);
            hasWhitespaceErrors = tracked.ExitCode == 1 || tracked.StandardOutput.Length > 0;

            foreach (var path in untrackedPaths)
            {
                var result = await RepositoryInspector.RunGitReadOnlyCheckAsync(
                    context.RepositoryRoot,
                    [
                        "diff", "--no-ext-diff", "--no-textconv", "--no-index", "--check", "--",
                        GitArgvPolicy.EmptyFilePath, path,
                    ],
                    safety.FilterDrivers,
                    cancellationToken,
                    safety.CoreWhitespace).ConfigureAwait(false);
                Accumulate(result.StandardOutput, output, ref outputTruncated);
                hasWhitespaceErrors |= result.StandardOutput.Length > 0;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            errors.Append("Whitespace inspection was cancelled.");
        }
        catch (TimeoutException ex)
        {
            timedOut = true;
            errors.Append(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            crashed = true;
            errors.Append(ex.Message);
        }

        return CommandResult(
            IBaselineVerification.WhitespaceCommandId,
            WhitespaceArguments(context.BaselineCommit),
            context.RepositoryRoot,
            timedOut || cancelled || crashed ? null : hasWhitespaceErrors ? 1 : 0,
            timer.Elapsed,
            output.ToString(),
            errors.ToString(),
            mandatory: false,
            timedOut,
            cancelled,
            crashed,
            outputTruncated);
    }

    private static IReadOnlyList<string> WhitespaceArguments(string baselineCommit) =>
        ["diff", "--no-ext-diff", "--no-textconv", "--check", baselineCommit, "--"];


    private static string ValidateContext(BaselineVerificationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RequestId == Guid.Empty)
        {
            throw new ArgumentException("Request id must identify the owning assignment.", nameof(context));
        }
        if (context.WorkspaceBindingId == Guid.Empty || context.BindingValidationRevision <= 0)
        {
            throw new ArgumentException("Workspace binding identity and revision are required.", nameof(context));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(context.RepositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.BaselineCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CurrentBranchOrHead);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.PolicyRevision);
        if (!Path.IsPathFullyQualified(context.RepositoryRoot))
        {
            throw new ArgumentException("Repository root must be an absolute canonical path.", nameof(context));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryRoot));
    }

    private static IReadOnlyList<string> ParsePaths(string value, string repositoryRoot) =>
        value.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => RepositoryInspector.EnsureSafeRepositoryPath(repositoryRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static bool SamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }

    private static void Accumulate(string value, StringBuilder destination, ref bool truncated)
    {
        var remaining = MaxOutputBytes - destination.Length;
        if (remaining <= 0)
        {
            truncated |= value.Length > 0;
            return;
        }

        destination.Append(value.AsSpan(0, Math.Min(remaining, value.Length)));
        truncated |= value.Length > remaining;
    }

    private static VerificationCommandResult CommandResult(
        string commandId,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int? exitCode,
        TimeSpan duration,
        string standardOutput,
        string standardError,
        bool mandatory,
        bool timedOut = false,
        bool cancelled = false,
        bool crashed = false,
        bool outputTruncated = false) => new(
            commandId,
            "git",
            arguments,
            workingDirectory,
            exitCode,
            duration,
            standardOutput,
            standardError,
            timedOut,
            cancelled,
            crashed,
            outputTruncated,
            ArtifactPath: null,
            mandatory);

    private CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);
        return deadline;
    }


    private int TimeoutSeconds =>
        Math.Max(1, (int)Math.Ceiling(_deadline.TotalSeconds));

    private async Task NotifyCommandStartingAsync(
        BaselineVerificationContext context,
        string commandId,
        bool mandatory,
        CancellationToken cancellationToken)
    {
        if (context.OnCommandStarting is null)
        {
            return;
        }

        await context.OnCommandStarting(
            new VerificationCommandStarting(commandId, mandatory, TimeoutSeconds),
            cancellationToken).ConfigureAwait(false);
    }

    private static class Native
    {
        public const int StatxSize = 256;
        public const int AtEmptyPath = 0x1000;
        public const uint StatxBasicStats = 0x7ff;
        public const int Enoent = 2;
        public const int Enxio = 6;
        public const int Enotdir = 20;
        public const ushort FileTypeMask = 0xF000;
        public const ushort RegularFile = 0x8000;
        public const ushort DirectoryFile = 0x4000;

        private const int ReadOnly = 0;
        private const int NoControllingTty = 0x100;
        private const int NonBlocking = 0x800;
        private const int CloseOnExec = 0x80000;

        private static readonly int NoFollow =
            RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64
                ? 0x8000
                : 0x20000;

        public static readonly int OpenContentReadFlags =
            ReadOnly | NoControllingTty | NonBlocking | CloseOnExec | NoFollow;

        [DllImport("libc", SetLastError = true)]
        public static extern int open(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags,
            int mode);

        [DllImport("libc", SetLastError = true)]
        public static extern int statx(
            int directoryDescriptor,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags,
            uint mask,
            byte[] buffer);
    }

    private sealed record RepositorySnapshot(
        string Root,
        string BaselineCommit,
        string Branch,
        string Head,
        IReadOnlyList<string> IndexEntries,
        string UnmergedIndex,
        IReadOnlyList<string> GitlinkPaths,
        IReadOnlyList<string> ContentPaths,
        IReadOnlyList<string> UntrackedPaths);

    private sealed record FileIdentity(string Path, string Kind, string ContentHash, int GitFileMode);
}

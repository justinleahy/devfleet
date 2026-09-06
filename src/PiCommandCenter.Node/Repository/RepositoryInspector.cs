using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Security;
using PiCommandCenter.Node.Verification;

namespace PiCommandCenter.Node.Repository;

/// <summary>
/// Records branch/base commit/status/diff using git argument lists, attributes paths to
/// lease owners, and detects unattributed external changes. Never mutates git.
/// </summary>
public sealed class RepositoryInspector : IRepositoryInspector
{
    private const int MaxFilterDrivers = 128;
    private const int MaxFilterDriverLength = 128;
    private const int MaxCoreWhitespaceLength = 256;
    private const int MaxLooseRefFiles = 8192;
    private const int MaxLooseRefDirectories = 1024;

    public async Task<RepositoryBaseline> CaptureBaselineAsync(
        string repositoryRoot,
        bool requireCleanStart,
        bool allowUntrackedFiles,
        CancellationToken cancellationToken)
    {
        var safety = await ReadRepositoryGitSafetyConfigAsync(repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        var branch = (await RunGitReadOnlyAsync(
            repositoryRoot,
            ["rev-parse", "--abbrev-ref", "HEAD"],
            safety.FilterDrivers,
            cancellationToken,
            safety.CoreWhitespace)
            .ConfigureAwait(false)).Trim();
        var commit = (await RunGitReadOnlyAsync(
            repositoryRoot,
            ["rev-parse", "HEAD"],
            safety.FilterDrivers,
            cancellationToken,
            safety.CoreWhitespace)
            .ConfigureAwait(false)).Trim();
        var status = await RunGitReadOnlyAsync(
            repositoryRoot,
            ["status", "--porcelain=v1", "-z"],
            safety.FilterDrivers,
            cancellationToken,
            safety.CoreWhitespace)
            .ConfigureAwait(false);
        var dirty = ParsePorcelainPaths(status);
        var blocking = allowUntrackedFiles
            ? dirty.Where(p => !p.StartsWith("?? ", StringComparison.Ordinal))
                .Select(StripStatusPrefix)
                .ToArray()
            : dirty.Select(StripStatusPrefix).ToArray();
        var isClean = blocking.Length == 0;
        if (requireCleanStart && !isClean)
        {
            throw new RepositoryDirtyException(blocking);
        }

        return new RepositoryBaseline(branch, commit, status, isClean, blocking);
    }

    public async Task<RepositoryDiffInspection> InspectDiffAsync(
        string repositoryRoot,
        string baseCommit,
        IReadOnlyList<ReservationLeaseInfo> leases,
        CancellationToken cancellationToken)
    {
        var safety = await ReadRepositoryGitSafetyConfigAsync(repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        var branch = (await RunGitReadOnlyAsync(
            repositoryRoot,
            ["rev-parse", "--abbrev-ref", "HEAD"],
            safety.FilterDrivers,
            cancellationToken,
            safety.CoreWhitespace).ConfigureAwait(false)).Trim();
        var unstaged = await RunGitReadOnlyAsync(
            repositoryRoot,
            ["diff", "--no-ext-diff", "--no-textconv", "--name-only", "-z", baseCommit, "--"],
            safety.FilterDrivers,
            cancellationToken,
            safety.CoreWhitespace).ConfigureAwait(false);
        var untracked = await RunGitReadOnlyAsync(
            repositoryRoot,
            ["ls-files", "--others", "--exclude-standard", "-z"],
            safety.FilterDrivers,
            cancellationToken,
            safety.CoreWhitespace).ConfigureAwait(false);

        var paths = ParseNullSeparated(unstaged)
            .Concat(ParseNullSeparated(untracked))
            .Select(path => EnsureSafeRepositoryPath(repositoryRoot, path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var changed = new List<ChangedFileAttribution>(paths.Length);
        var unattributed = new List<string>();
        foreach (var path in paths)
        {
            var owner = FindOwner(path, leases);
            changed.Add(new ChangedFileAttribution(path, owner?.OwnerSessionId, owner?.LeaseId));
            if (owner is null)
            {
                unattributed.Add(path);
            }
        }

        return new RepositoryDiffInspection(branch, baseCommit, changed, unattributed);
    }

    public async Task DetectExternalChangesAsync(
        string repositoryRoot,
        string baseCommit,
        IReadOnlyList<ReservationLeaseInfo> leases,
        CancellationToken cancellationToken)
    {
        var inspection = await InspectDiffAsync(repositoryRoot, baseCommit, leases, cancellationToken)
            .ConfigureAwait(false);
        if (inspection.UnattributedPaths.Count > 0)
        {
            throw new ExternalRepositoryModificationException(inspection.UnattributedPaths);
        }
    }

    internal static async Task<string> RunGitReadOnlyAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> filterDrivers,
        CancellationToken cancellationToken,
        string? coreWhitespace = null)
    {
        var result = await RunGitReadOnlyAsync(
            repositoryRoot,
            arguments,
            filterDrivers,
            allowExitOne: false,
            sanitizeOutput: false,
            cancellationToken,
            coreWhitespace).ConfigureAwait(false);
        return result.StandardOutput;
    }

    internal static Task<GitReadResult> RunGitReadOnlyCheckAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> filterDrivers,
        CancellationToken cancellationToken,
        string? coreWhitespace = null) =>
        RunGitReadOnlyAsync(
            repositoryRoot,
            arguments,
            filterDrivers,
            allowExitOne: true,
            sanitizeOutput: true,
            cancellationToken,
            coreWhitespace);

    private static async Task<GitReadResult> RunGitReadOnlyAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> filterDrivers,
        bool allowExitOne,
        bool sanitizeOutput,
        CancellationToken cancellationToken,
        string? coreWhitespace = null)
    {
        const int maxOutputBytes = 1024 * 1024;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var processArguments = GitArgvPolicy.AddProcessSafetyOptions(arguments);
        var safetyArgumentCount = processArguments.Count - arguments.Count;
        for (var index = 0; index < safetyArgumentCount; index++)
        {
            startInfo.ArgumentList.Add(processArguments[index]);
        }
        AddFilterSafetyArguments(startInfo, filterDrivers);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var hostSecrets = sanitizeOutput
            ? VerificationProcessEnvironment.CollectHostSecretValues()
            : [];
        var sandbox = VerificationProcessEnvironment.CreateSandbox();
        try
        {
            VerificationProcessEnvironment.ApplyMinimal(startInfo, sandbox);
            ApplyGitSafetyEnvironment(
                startInfo,
                sandbox,
                root,
                filterDrivers,
                coreWhitespace,
                cancellationToken);
            VerificationProcessSandbox.Apply(startInfo, root, sandbox);

            using var ownedProcess = await OwnedProcess.StartAsync(startInfo).ConfigureAwait(false);
            var process = ownedProcess.Process;
            process.StandardInput.Close();
            var standardOutput = ReadBoundedAsync(
                process.StandardOutput.BaseStream,
                maxOutputBytes,
                strictUtf8: !sanitizeOutput);
            var standardError = ReadBoundedAsync(
                process.StandardError.BaseStream,
                maxOutputBytes,
                strictUtf8: false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await ownedProcess.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillTree(process);
                await WaitForExitQuietlyAsync(ownedProcess).ConfigureAwait(false);
                await DrainBoundedReadsAsync(standardOutput, standardError).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("Git inspection timed out.");
            }

            var output = await CompleteBoundedReadAsync(standardOutput).ConfigureAwait(false);
            var error = await CompleteBoundedReadAsync(standardError).ConfigureAwait(false);
            if (output.Truncated || error.Truncated)
            {
                throw new InvalidOperationException("Git inspection output exceeded the permitted limit.");
            }
            if (process.ExitCode != 0)
            {
                if (!allowExitOne || process.ExitCode is not (1 or 3))
                {
                    throw new InvalidOperationException(
                        $"Git {arguments[0]} inspection failed with exit code {process.ExitCode}.");
                }
            }

            return new GitReadResult(
                sanitizeOutput
                    ? Sanitize(output.Text, maxOutputBytes, sandbox, hostSecrets)
                    : output.Text,
                process.ExitCode == 0 ? 0 : 1);
        }
        finally
        {
            VerificationProcessEnvironment.TryDeleteSandbox(sandbox);
        }
    }

    internal sealed record RepositoryGitSafetyConfig(
        IReadOnlyList<string> FilterDrivers,
        string? CoreWhitespace);

    internal static async Task<IReadOnlyList<string>> FindRepositoryFilterDriversAsync(
        string repositoryRoot,
        CancellationToken cancellationToken) =>
        (await ReadRepositoryGitSafetyConfigAsync(repositoryRoot, cancellationToken).ConfigureAwait(false))
            .FilterDrivers;

    internal static Task<RepositoryGitSafetyConfig> ReadRepositoryGitSafetyConfigAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        const int maxConfigBytes = 1024 * 1024;
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var gitDirectory = ResolveGitDirectory(root);

        var commonDirectory = gitDirectory;
        var commonDirectoryPointer = Path.Combine(gitDirectory, "commondir");
        if (GitMetadataPathExists(commonDirectoryPointer))
        {
            var pointer = ReadRegularGitMetadataUtf8(commonDirectoryPointer, 4096);
            commonDirectory = Path.GetFullPath(Path.Combine(gitDirectory, pointer.Trim()));
        }

        var filterDrivers = new SortedSet<string>(StringComparer.Ordinal);
        string? coreWhitespace = null;
        string? currentSection = null;
        long inspectedBytes = 0;
        foreach (var configPath in new[]
        {
            Path.Combine(commonDirectory, "config"),
            Path.Combine(gitDirectory, "config.worktree"),
        })
        {
            if (!GitMetadataPathExists(configPath))
            {
                continue;
            }

            var text = ReadRegularGitMetadataUtf8(configPath, maxConfigBytes);
            inspectedBytes += Encoding.UTF8.GetByteCount(text);
            if (inspectedBytes > maxConfigBytes)
            {
                throw new InvalidOperationException("Repository Git configuration exceeded the permitted limit.");
            }

            foreach (var line in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] is '#' or ';')
                {
                    continue;
                }
                if (trimmed.EndsWith('\\'))
                {
                    throw new InvalidOperationException("Repository Git configuration is ambiguous.");
                }
                if (trimmed[0] == '[')
                {
                    if (trimmed[^1] != ']')
                    {
                        throw new InvalidOperationException("Repository Git configuration is malformed.");
                    }

                    var section = trimmed[1..^1].Trim();
                    currentSection = section;
                    if (section.StartsWith("include", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Included repository Git configuration is not permitted.");
                    }
                    if (section.StartsWith("filter", StringComparison.OrdinalIgnoreCase))
                    {
                        var driver = ParseFilterDriver(section);
                        if (driver.Length > MaxFilterDriverLength
                            || driver.Any(static character =>
                                !char.IsAsciiLetterOrDigit(character)
                                && character is not ('-' or '_' or '.')))
                        {
                            throw new InvalidOperationException(
                                "Repository filter configuration contains an unsupported driver name.");
                        }
                        if (!filterDrivers.Contains(driver)
                            && filterDrivers.Count >= MaxFilterDrivers)
                        {
                            throw new InvalidOperationException(
                                "Repository Git configuration declares too many filter drivers.");
                        }
                        filterDrivers.Add(driver);
                    }

                    continue;
                }

                if (!string.Equals(currentSection, "core", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = trimmed[..separator].Trim();
                if (!string.Equals(key, "whitespace", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parsed = SanitizeCoreWhitespace(trimmed[(separator + 1)..].Trim());
                if (parsed is not null)
                {
                    coreWhitespace = parsed;
                }
            }
        }

        return Task.FromResult(new RepositoryGitSafetyConfig(filterDrivers.ToArray(), coreWhitespace));
    }

    private static string? SanitizeCoreWhitespace(string raw)
    {
        if (raw.Length is 0 or > MaxCoreWhitespaceLength)
        {
            return null;
        }

        if (raw[0] is '"' && raw[^1] is '"')
        {
            raw = raw[1..^1];
        }

        var kept = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var negated = part.StartsWith('-');
            var token = negated ? part[1..] : part;
            if (token.StartsWith("tabwidth=", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(token["tabwidth=".Length..], out var width)
                    || width is < 1 or > 64)
                {
                    continue;
                }

                kept.Add(negated ? $"-tabwidth={width}" : $"tabwidth={width}");
                continue;
            }

            if (!IsAllowlistedWhitespaceToken(token))
            {
                continue;
            }

            kept.Add(negated ? "-" + token : token);
        }

        return kept.Count == 0 ? null : string.Join(',', kept);
    }

    private static bool IsAllowlistedWhitespaceToken(string token) =>
        token.Equals("blank-at-eol", StringComparison.OrdinalIgnoreCase)
        || token.Equals("blank-at-eof", StringComparison.OrdinalIgnoreCase)
        || token.Equals("space-before-tab", StringComparison.OrdinalIgnoreCase)
        || token.Equals("indent-with-non-tab", StringComparison.OrdinalIgnoreCase)
        || token.Equals("tab-in-indent", StringComparison.OrdinalIgnoreCase)
        || token.Equals("cr-at-eol", StringComparison.OrdinalIgnoreCase)
        || token.Equals("trailing-space", StringComparison.OrdinalIgnoreCase);

    private static string ResolveGitDirectory(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (IsSymlinkOrReparsePoint(dotGit))
        {
            throw new InvalidOperationException("Repository Git metadata cannot be inspected safely.");
        }
        if (Directory.Exists(dotGit))
        {
            return dotGit;
        }

        var pointer = ReadRegularGitMetadataUtf8(dotGit, 4096).Trim();
        const string prefix = "gitdir:";
        if (!pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Repository Git metadata is malformed.");
        }
        return Path.GetFullPath(Path.Combine(root, pointer[prefix.Length..].Trim()));
    }

    private static bool GitMetadataPathExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (IOException)
        {
            return true;
        }
    }


    private static string ReadRegularGitMetadataUtf8(string path, int maxBytes)
    {
        using var handle = OpenGitMetadataHandle(path);
        if (OperatingSystem.IsLinux())
        {
            var fileType = GetLinuxFileType(handle);
            if (fileType != Native.RegularFile)
            {
                throw new InvalidOperationException("Repository Git metadata cannot be inspected safely.");
            }
        }
        else
        {
            var metadata = new FileInfo(path);
            if (metadata.LinkTarget is not null
                || (metadata.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Repository Git metadata cannot be inspected safely.");
            }
        }

        var length = RandomAccess.GetLength(handle);
        if (length < 0 || length > maxBytes)
        {
            throw new InvalidOperationException("Repository Git metadata cannot be inspected safely.");
        }

        var buffer = new byte[length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(offset), offset);
            if (read == 0)
            {
                break;
            }
            offset += read;
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer.AsSpan(0, offset));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("Repository Git metadata was not valid UTF-8.", ex);
        }
    }

    private static SafeFileHandle OpenGitMetadataHandle(string path)
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

        var descriptor = Native.open(path, Native.OpenMetadataReadFlags, 0);
        if (descriptor >= 0)
        {
            return new SafeFileHandle(descriptor, ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        throw error switch
        {
            Native.Enoent => new FileNotFoundException("Repository Git metadata was not found.", path),
            Native.Enotdir => new DirectoryNotFoundException("A Git metadata path component was not found."),
            Native.Eloop => new InvalidOperationException("Repository Git metadata cannot be inspected safely."),
            Native.Enxio => new InvalidOperationException("Repository Git metadata cannot be inspected safely."),
            _ => new InvalidOperationException($"Repository Git metadata could not be opened (errno {error})."),
        };
    }

    private static ushort GetLinuxFileType(SafeFileHandle handle)
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
            throw new InvalidOperationException("Repository Git metadata type could not be verified.");
        }

        return (ushort)(MemoryMarshal.Read<ushort>(status.AsSpan(28)) & Native.FileTypeMask);
    }

    private static string ParseFilterDriver(string section)
    {
        const string prefix = "filter ";
        if (!section.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Repository filter configuration is malformed.");
        }

        var quoted = section[prefix.Length..].Trim();
        if (quoted.Length < 2 || quoted[0] != '"' || quoted[^1] != '"')
        {
            throw new InvalidOperationException("Repository filter configuration is malformed.");
        }

        var driver = new StringBuilder(quoted.Length - 2);
        for (var index = 1; index < quoted.Length - 1; index++)
        {
            var character = quoted[index];
            if (character != '\\')
            {
                driver.Append(character);
                continue;
            }
            if (++index >= quoted.Length - 1 || quoted[index] is not ('\\' or '"'))
            {
                throw new InvalidOperationException("Repository filter configuration is ambiguous.");
            }
            driver.Append(quoted[index]);
        }

        return driver.Length > 0
            ? driver.ToString()
            : throw new InvalidOperationException("Repository filter configuration is malformed.");
    }


    private static string Sanitize(
        string text,
        int maxCharacters,
        string sandboxRoot,
        IReadOnlyList<string> hostSecrets)
    {
        var value = text.Replace(sandboxRoot, "[sandbox]", StringComparison.Ordinal);
        foreach (var secret in hostSecrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        return DiagnosticSanitizer.Sanitize(value, maxCharacters);
    }

    private static void ApplyGitSafetyEnvironment(
        ProcessStartInfo startInfo,
        string sandboxRoot,
        string repositoryRoot,
        IReadOnlyList<string> filterDrivers,
        string? coreWhitespace,
        CancellationToken cancellationToken)
    {
        var overlay = CreateSanitizedGitOverlay(
            sandboxRoot,
            repositoryRoot,
            filterDrivers,
            coreWhitespace,
            cancellationToken);
        startInfo.Environment["GIT_DIR"] = overlay;
        startInfo.Environment["GIT_WORK_TREE"] = repositoryRoot;
        startInfo.Environment["GIT_ATTR_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_SYSTEM"] = GitArgvPolicy.EmptyFilePath;
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = GitArgvPolicy.EmptyFilePath;
        startInfo.Environment["GIT_NO_LAZY_FETCH"] = "1";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
    }

    private static string CreateSanitizedGitOverlay(
        string sandboxRoot,
        string repositoryRoot,
        IReadOnlyList<string> filterDrivers,
        string? coreWhitespace,
        CancellationToken cancellationToken)
    {
        var overlay = Directory.CreateDirectory(Path.Combine(sandboxRoot, "git")).FullName;
        var gitDirectory = ResolveGitDirectory(repositoryRoot);
        var commonDirectory = gitDirectory;
        var commonDirectoryPointer = Path.Combine(gitDirectory, "commondir");
        if (GitMetadataPathExists(commonDirectoryPointer))
        {
            var pointer = ReadRegularGitMetadataUtf8(commonDirectoryPointer, 4096);
            commonDirectory = Path.GetFullPath(Path.Combine(gitDirectory, pointer.Trim()));
        }

        var objects = Path.Combine(commonDirectory, "objects");
        if (!Directory.Exists(objects) || IsSymlinkOrReparsePoint(objects))
        {
            throw new InvalidOperationException("Repository Git objects cannot be inspected safely.");
        }

        Directory.CreateDirectory(Path.Combine(overlay, "objects", "info"));
        Directory.CreateDirectory(Path.Combine(overlay, "info"));
        File.WriteAllText(
            Path.Combine(overlay, "objects", "info", "alternates"),
            objects + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(overlay, "config"),
            BuildSanitizedGitConfig(filterDrivers, coreWhitespace),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllBytes(Path.Combine(overlay, "info", "attributes"), []);

        TryCopyRegularMetadataFile(
            Path.Combine(gitDirectory, "HEAD"),
            Path.Combine(overlay, "HEAD"),
            4096);
        TryCopyRegularMetadataFile(
            Path.Combine(gitDirectory, "index"),
            Path.Combine(overlay, "index"),
            32 * 1024 * 1024);
        TryCopyRegularMetadataFile(
            Path.Combine(commonDirectory, "packed-refs"),
            Path.Combine(overlay, "packed-refs"),
            32 * 1024 * 1024);
        TryCopyRefs(Path.Combine(gitDirectory, "refs"), Path.Combine(overlay, "refs"), cancellationToken);
        if (!string.Equals(gitDirectory, commonDirectory, StringComparison.Ordinal))
        {
            TryCopyRefs(
                Path.Combine(commonDirectory, "refs"),
                Path.Combine(overlay, "refs"),
                cancellationToken);
        }

        return overlay;
    }

    private static string BuildSanitizedGitConfig(IReadOnlyList<string> filterDrivers, string? coreWhitespace)
    {
        var config = new StringBuilder();
        config.Append("[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n");
        config.Append("\thooksPath = ").Append(GitArgvPolicy.EmptyFilePath).Append('\n');
        config.Append("\tattributesFile = ").Append(GitArgvPolicy.EmptyFilePath).Append('\n');
        config.Append("\tfsmonitor = false\n");
        if (!string.IsNullOrEmpty(coreWhitespace))
        {
            config.Append("\twhitespace = ").Append(coreWhitespace).Append('\n');
        }
        config.Append("[protocol]\n\tallow = never\n");
        config.Append("[protocol \"file\"]\n\tallow = never\n");
        config.Append("[protocol \"ext\"]\n\tallow = never\n");
        foreach (var driver in filterDrivers)
        {
            config.Append("[filter \"").Append(driver).Append("\"]\n");
            config.Append("\tclean =\n\tsmudge =\n\tprocess =\n\trequired = false\n");
        }

        return config.ToString();
    }

    private static void TryCopyRegularMetadataFile(string source, string destination, int maxBytes)
    {
        try
        {
            if (!GitMetadataPathExists(source))
            {
                return;
            }

            using var handle = OpenGitMetadataHandle(source);
            if (OperatingSystem.IsLinux() && GetLinuxFileType(handle) != Native.RegularFile)
            {
                throw new InvalidOperationException("Repository Git metadata cannot be inspected safely.");
            }

            var length = RandomAccess.GetLength(handle);
            if (length < 0 || length > maxBytes)
            {
                throw new InvalidOperationException("Repository Git metadata cannot be inspected safely.");
            }

            var buffer = new byte[length];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(offset), offset);
                if (read == 0)
                {
                    break;
                }
                offset += read;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, buffer.AsSpan(0, offset).ToArray());
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void TryCopyRefs(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source) || IsSymlinkOrReparsePoint(source))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(source);
        var directoryCount = 1;
        var fileCount = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSymlinkOrReparsePoint(entry))
                {
                    throw new InvalidOperationException("Repository Git metadata cannot be inspected safely.");
                }

                if (Directory.Exists(entry))
                {
                    if (++directoryCount > MaxLooseRefDirectories)
                    {
                        throw new InvalidOperationException(
                            "Repository Git metadata contains too many ref directories.");
                    }

                    pending.Push(entry);
                    continue;
                }

                if (++fileCount > MaxLooseRefFiles)
                {
                    throw new InvalidOperationException("Repository Git metadata contains too many loose refs.");
                }

                var relative = Path.GetRelativePath(source, entry);
                TryCopyRegularMetadataFile(entry, Path.Combine(destination, relative), 4096);
            }
        }
    }

    private static void AddFilterSafetyArguments(
        ProcessStartInfo startInfo,
        IReadOnlyList<string> filterDrivers)
    {
        if (filterDrivers.Count > MaxFilterDrivers)
        {
            throw new InvalidOperationException(
                "Repository Git configuration declares too many filter drivers.");
        }

        AddGitConfigArgument(startInfo, "core.attributesFile", GitArgvPolicy.EmptyFilePath);
        AddGitConfigArgument(startInfo, "protocol.allow", "never");
        AddGitConfigArgument(startInfo, "protocol.file.allow", "never");
        AddGitConfigArgument(startInfo, "protocol.ext.allow", "never");
        foreach (var driver in filterDrivers)
        {
            AddGitConfigArgument(startInfo, $"filter.{driver}.clean", string.Empty);
            AddGitConfigArgument(startInfo, $"filter.{driver}.smudge", string.Empty);
            AddGitConfigArgument(startInfo, $"filter.{driver}.process", string.Empty);
            AddGitConfigArgument(startInfo, $"filter.{driver}.required", "false");
        }
    }

    private static void AddGitConfigArgument(
        ProcessStartInfo startInfo,
        string key,
        string value)
    {
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"{key}={value}");
    }

    private static async Task<BoundedRead> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        bool strictUtf8)
    {
        var buffer = new byte[4096];
        using var collected = new MemoryStream();
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maxBytes - (int)collected.Length;
            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }

            var take = Math.Min(remaining, read);
            collected.Write(buffer, 0, take);
            truncated |= take < read;
        }

        try
        {
            var encoding = strictUtf8
                ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                : Encoding.UTF8;
            return new BoundedRead(encoding.GetString(collected.ToArray()), truncated);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("Git inspection output was not valid UTF-8.", ex);
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task WaitForExitQuietlyAsync(OwnedProcess process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static async Task DrainBoundedReadsAsync(Task<BoundedRead> standardOutput, Task<BoundedRead> standardError)
    {
        try
        {
            await Task.WhenAll(standardOutput, standardError)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static async Task<BoundedRead> CompleteBoundedReadAsync(Task<BoundedRead> read)
    {
        try
        {
            return await read.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Git inspection timed out.");
        }
    }

    private static ReservationLeaseInfo? FindOwner(string path, IReadOnlyList<ReservationLeaseInfo> leases)
    {
        foreach (var lease in leases)
        {
            if (!string.Equals(lease.State, "Active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var scope in lease.Scopes)
            {
                if (Covers(scope, path))
                {
                    return lease;
                }
            }
        }

        return null;
    }

    private static bool Covers(ReservationScopeSpec scope, string path)
    {
        var kind = scope.Kind;
        var prefix = scope.Path.Trim().TrimEnd('/');
        if (kind.Equals("file", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("File", StringComparison.Ordinal))
        {
            return string.Equals(prefix, path, StringComparison.Ordinal);
        }

        if (kind.Equals("directory", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Directory", StringComparison.Ordinal))
        {
            return string.Equals(path, prefix, StringComparison.Ordinal)
                || path.StartsWith(prefix + "/", StringComparison.Ordinal);
        }

        return false;
    }

    internal static string EnsureSafeRepositoryPath(string repositoryRoot, string gitPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitPath);

        var normalized = OperatingSystem.IsWindows() ? gitPath.Replace('\\', '/') : gitPath;
        var firstSegmentEnd = normalized.IndexOf('/');
        var firstSegment = firstSegmentEnd < 0 ? normalized : normalized[..firstSegmentEnd];
        if (string.Equals(firstSegment, ".git", StringComparison.OrdinalIgnoreCase)
            || Path.IsPathRooted(gitPath))
        {
            throw new InvalidOperationException("Repository path targets protected or external metadata.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var fullPath = Path.GetFullPath(Path.Combine(root, gitPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("Repository path leaves the canonical workspace.");
        }

        var current = root;
        if (IsSymlinkOrReparsePoint(current))
        {
            throw new InvalidOperationException("Repository path crosses a symbolic link or reparse point.");
        }
        foreach (var segment in Path.GetRelativePath(root, fullPath)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsSymlinkOrReparsePoint(current))
            {
                throw new InvalidOperationException("Repository path crosses a symbolic link or reparse point.");
            }
        }

        return normalized;
    }

    private static bool IsSymlinkOrReparsePoint(string path)
    {
        var file = new FileInfo(path);
        var directory = new DirectoryInfo(path);
        if (file.LinkTarget is not null || directory.LinkTarget is not null)
        {
            return true;
        }

        return (file.Exists || directory.Exists)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static IReadOnlyList<string> ParsePorcelainPaths(string status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return [];
        }

        return ParseNullSeparated(status);
    }

    private static IReadOnlyList<string> ParseNullSeparated(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    internal static IReadOnlyList<string> CanonicalizeNullSeparatedRecords(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(record => record, StringComparer.Ordinal)
            .ToArray();
    }

    private static string StripStatusPrefix(string porcelain)
    {
        if (porcelain.Length >= 3 && porcelain[2] == ' ')
        {
            return porcelain[3..];
        }

        return porcelain;
    }

    internal readonly record struct GitReadResult(string StandardOutput, int ExitCode);

    private readonly record struct BoundedRead(string Text, bool Truncated);


    private static class Native
    {
        public const int StatxSize = 256;
        public const int AtEmptyPath = 0x1000;
        public const uint StatxBasicStats = 0x7ff;
        public const int Enoent = 2;
        public const int Enxio = 6;
        public const int Enotdir = 20;
        public const int Eloop = 40;
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

        public static readonly int OpenMetadataReadFlags =
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
}

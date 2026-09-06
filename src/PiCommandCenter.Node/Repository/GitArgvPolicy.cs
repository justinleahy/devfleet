namespace PiCommandCenter.Node.Repository;

/// <summary>
/// Supervisor-owned Git is read-only for inspection. Destructive verbs are rejected
/// before a process is started (SPEC §3.7, §19.4).
/// </summary>
public static class GitArgvPolicy
{
    private static readonly string HooksPath = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    public static void EnsureReadOnly(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!IsAllowed(arguments))
        {
            var command = arguments.Count == 0 ? "<empty>" : string.Join(' ', arguments);
            throw new InvalidOperationException(
                $"Git argv '{command}' is not permitted. Inspection never mutates the repository.");
        }
    }

    internal static IReadOnlyList<string> AddProcessSafetyOptions(IReadOnlyList<string> arguments)
    {
        EnsureReadOnly(arguments);
        return
        [
            "--no-optional-locks",
            "-c",
            $"core.hooksPath={HooksPath}",
            "-c",
            "core.fsmonitor=false",
            .. arguments,
        ];
    }

    internal static string EmptyFilePath => HooksPath;

    private static bool IsAllowed(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return false;
        }

        return arguments[0] switch
        {
            "rev-parse" => IsRevParse(arguments),
            "status" => Matches(arguments, "status", "--porcelain=v1", "-z"),
            "diff" => IsDiff(arguments),
            "ls-files" => IsLsFiles(arguments),
            _ => false,
        };
    }

    private static bool IsRevParse(IReadOnlyList<string> arguments) =>
        Matches(arguments, "rev-parse", "HEAD")
        || Matches(arguments, "rev-parse", "--abbrev-ref", "HEAD")
        || Matches(arguments, "rev-parse", "--show-toplevel")
        || (arguments.Count == 3
            && arguments[1] == "--verify"
            && IsOperand(arguments[2]));

    private static bool IsDiff(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 7
            && arguments[1] == "--no-ext-diff"
            && arguments[2] == "--no-textconv"
            && arguments[3] == "--name-only"
            && arguments[4] == "-z"
            && IsOperand(arguments[5])
            && arguments[6] == "--")
        {
            return true;
        }

        if (arguments.Count == 6
            && arguments[1] == "--no-ext-diff"
            && arguments[2] == "--no-textconv"
            && arguments[3] == "--check"
            && IsOperand(arguments[4])
            && arguments[5] == "--")
        {
            return true;
        }

        return arguments.Count == 8
            && arguments[1] == "--no-ext-diff"
            && arguments[2] == "--no-textconv"
            && arguments[3] == "--no-index"
            && arguments[4] == "--check"
            && arguments[5] == "--"
            && arguments[6] == EmptyFilePath
            && IsPathOperand(arguments[7]);
    }

    private static bool IsLsFiles(IReadOnlyList<string> arguments) =>
        Matches(arguments, "ls-files", "--others", "--exclude-standard", "-z")
        || Matches(arguments, "ls-files", "--cached", "-z")
        || Matches(arguments, "ls-files", "--stage", "-z")
        || Matches(arguments, "ls-files", "-u", "-z");

    private static bool IsOperand(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.StartsWith('-')
        && !value.Contains('\0');

    private static bool IsPathOperand(string value) =>
        IsOperand(value)
        && !Path.IsPathRooted(value);

    private static bool Matches(IReadOnlyList<string> actual, params string[] expected)
    {
        if (actual.Count != expected.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (!string.Equals(actual[index], expected[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

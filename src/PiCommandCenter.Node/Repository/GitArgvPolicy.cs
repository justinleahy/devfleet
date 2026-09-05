namespace PiCommandCenter.Node.Repository;

/// <summary>
/// Supervisor-owned Git is read-only for inspection. Destructive verbs are rejected
/// before a process is started (SPEC §3.7, §19.4).
/// </summary>
public static class GitArgvPolicy
{
    private static readonly HashSet<string> AllowedVerbs = new(StringComparer.Ordinal)
    {
        "rev-parse",
        "status",
        "diff",
        "ls-files",
    };

    private static readonly HashSet<string> ForbiddenVerbs = new(StringComparer.Ordinal)
    {
        "add",
        "commit",
        "reset",
        "checkout",
        "switch",
        "stash",
        "clean",
        "merge",
        "rebase",
        "worktree",
        "push",
        "pull",
        "fetch",
        "cherry-pick",
        "revert",
        "am",
        "gc",
    };

    public static void EnsureReadOnly(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            throw new InvalidOperationException("Git argument list must not be empty.");
        }

        var verb = arguments[0];
        if (verb.StartsWith('-'))
        {
            throw new InvalidOperationException("Git argument list must start with a subcommand.");
        }

        if (ForbiddenVerbs.Contains(verb) || !AllowedVerbs.Contains(verb))
        {
            throw new InvalidOperationException(
                $"Git subcommand '{verb}' is not permitted. Inspection never mutates the repository.");
        }

        foreach (var argument in arguments)
        {
            if (argument is "--hard" or "--soft" or "--mixed" or "--force" or "-f"
                or "--force-with-lease")
            {
                throw new InvalidOperationException(
                    $"Git argument '{argument}' is not permitted.");
            }
        }
    }
}

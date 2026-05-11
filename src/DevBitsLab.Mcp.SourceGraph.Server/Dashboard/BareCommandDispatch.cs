namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Factored bare-command dispatch helper, per design Decision 2. Detects whether <c>args</c>
/// is a bare invocation (no positional subcommand AND no <c>--help</c>/<c>-h</c> flag), and if
/// so rewrites it to either <c>["dashboard"]</c> or <c>["status"]</c> based on the stdin
/// disposition.
///
/// <para>
/// Carved out as a pure helper so tests can drive the dispatch logic directly without spawning
/// a child process: the production environment probe (<see cref="Console.IsInputRedirected"/>,
/// <see cref="Environment.UserInteractive"/>) is injected as a delegate.
/// </para>
/// </summary>
internal static class BareCommandDispatch
{
    /// <summary>
    /// Compute the args array to feed into <see cref="Cli.CommandLine.Parse"/>. When
    /// <paramref name="args"/> is bare, prepends either <c>dashboard</c> (every stdio stream
    /// attached to a tty AND <c>UserInteractive</c>) or <c>status</c> (any stream redirected,
    /// or running non-interactively).
    ///
    /// <para>
    /// The probe checks stdio disposition (not just stdin): the dashboard needs stdin for key
    /// input AND stdout + stderr to position the ANSI cursor. The production probe
    /// <see cref="IsStdioRedirectedOrNonInteractive"/> bundles all four checks; the parameter
    /// here is named <paramref name="isStdioRedirectedOrNonInteractive"/> to mirror the
    /// production probe and keep tests honest about what they're stubbing.
    /// </para>
    ///
    /// <para>
    /// Bare-detection rules: <c>args.Length == 0</c> OR the first non-flag positional is empty.
    /// Crucially we skip the rewrite when <c>--help</c>/<c>-h</c> is present so the existing help
    /// path stays reachable without typing a subcommand. Other flags pass through unchanged.
    /// </para>
    /// </summary>
    public static string[] Rewrite(string[] args, Func<bool> isStdioRedirectedOrNonInteractive)
    {
        if (HasHelpFlag(args)) return args;
        if (!IsBare(args)) return args;
        var target = isStdioRedirectedOrNonInteractive() ? "status" : "dashboard";
        // Prepend the verb; any flags already present propagate to the new subcommand.
        var rewritten = new string[args.Length + 1];
        rewritten[0] = target;
        Array.Copy(args, 0, rewritten, 1, args.Length);
        return rewritten;
    }

    /// <summary>
    /// Production probe: returns true when ANY stdio stream is redirected or the process is
    /// running non-interactively. The dashboard needs all three streams attached to a tty (stdin
    /// for key input; stdout + stderr for ANSI cursor positioning). If any stream is a pipe or
    /// file, we route to <c>status</c> instead — the headless surface that prints once and exits.
    ///
    /// <para>
    /// The previous version only checked stdin redirection, which mis-classified
    /// <c>sourcegraph-mcp | cat</c> (stdin still a tty, stdout piped) as interactive and launched
    /// the dashboard — its ANSI redraw codes then poured into the pipe instead of dispatching to
    /// <c>status</c> as documented. The method name reflects the broader contract: "is ANY stdio
    /// redirected, or are we non-interactive?".
    /// </para>
    /// </summary>
    public static bool IsStdioRedirectedOrNonInteractive()
    {
        try
        {
            if (Console.IsInputRedirected) return true;
            if (Console.IsOutputRedirected) return true;
            if (Console.IsErrorRedirected) return true;
            // Environment.UserInteractive is false for service-account / non-tty contexts; we
            // route those to `status` because the dashboard requires a tty to be useful.
            return !Environment.UserInteractive;
        }
        catch (IOException)
        {
            // Console probe failed (unusual; can happen on detached stdio). Conservative fallback
            // to "redirected" → `status`, the headless surface.
            return true;
        }
    }

    private static bool HasHelpFlag(string[] args)
    {
        foreach (var a in args)
        {
            if (a is "-h" or "--help") return true;
        }
        return false;
    }

    /// <summary>
    /// "Bare" means: no positional subcommand anywhere in <paramref name="args"/>. We walk the
    /// whole list, skipping over value-bearing flag tokens (a flag like <c>--root /repo</c>
    /// consumes two tokens), and return <c>true</c> only if every token was consumed as a flag
    /// or as a flag's value. Any positional (non-<c>-</c>) token that isn't sitting in a value
    /// slot is a subcommand and disqualifies the bare-rewrite path.
    ///
    /// <para>
    /// This composes correctly with bare invocations like <c>sourcegraph-mcp --root /repo</c>
    /// (still bare → rewrite to <c>dashboard --root /repo</c>) and with subcommand-bearing
    /// invocations like <c>sourcegraph-mcp --root /repo serve</c> (NOT bare → pass through to
    /// <c>serve</c>). The set of value-bearing flag names mirrors what
    /// <see cref="Cli.CommandLine.Parse"/> calls <c>RequireArg</c> / <c>RequirePositiveInt</c>
    /// on — kept in sync manually because cross-class coupling would be heavier than the
    /// duplication.
    /// </para>
    /// </summary>
    internal static bool IsBare(string[] args)
    {
        if (args.Length == 0) return true;
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith('-'))
            {
                // A positional we didn't consume as a flag's value → this is a subcommand.
                return false;
            }
            // Flag. Consume one extra token if this is a value-bearing flag.
            if (ValueBearingFlags.Contains(token))
            {
                i++; // skip the value
            }
        }
        return true;
    }

    /// <summary>
    /// Flags that <see cref="Cli.CommandLine.Parse"/> consumes a positional value after.
    /// Adding a new value-bearing flag in <c>CommandLine</c> requires a matching entry here
    /// (or the bare-detection misclassifies its value as a subcommand). Boolean flags are not
    /// listed.
    /// </summary>
    private static readonly HashSet<string> ValueBearingFlags = new(StringComparer.Ordinal)
    {
        "--solution", "-s",
        "--db",
        "--model",
        "--root",
        "--scope",
        "--query-timeout-seconds",
        "--query-row-limit",
        "--install-mode",
        "--client",
        "--watch-interval",
        "--activity-bytes",
    };
}

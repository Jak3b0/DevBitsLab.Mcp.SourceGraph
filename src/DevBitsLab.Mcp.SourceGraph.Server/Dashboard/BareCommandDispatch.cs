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
    /// <paramref name="args"/> is bare, prepends either <c>dashboard</c> (tty) or
    /// <c>status</c> (redirected stdin / non-interactive).
    ///
    /// <para>
    /// Bare-detection rules: <c>args.Length == 0</c> OR the first non-flag positional is empty.
    /// Crucially we skip the rewrite when <c>--help</c>/<c>-h</c> is present so the existing help
    /// path stays reachable without typing a subcommand. Other flags pass through unchanged.
    /// </para>
    /// </summary>
    public static string[] Rewrite(string[] args, Func<bool> isStdinRedirectedOrNonInteractive)
    {
        if (HasHelpFlag(args)) return args;
        if (!IsBare(args)) return args;
        var target = isStdinRedirectedOrNonInteractive() ? "status" : "dashboard";
        // Prepend the verb; any flags already present propagate to the new subcommand.
        var rewritten = new string[args.Length + 1];
        rewritten[0] = target;
        Array.Copy(args, 0, rewritten, 1, args.Length);
        return rewritten;
    }

    /// <summary>
    /// Production probe: stdin-is-tty equivalent. True when stdin is a pipe/file OR the process
    /// is running without user interaction (service-account / non-tty contexts).
    /// </summary>
    public static bool IsStdinRedirectedOrNonInteractive()
    {
        try
        {
            if (Console.IsInputRedirected) return true;
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
    /// "Bare" means: no positional subcommand. Anything that doesn't start with `-` and isn't
    /// itself a flag value is a positional. We don't try to be clever about flag-value pairing
    /// (e.g. `--root /x` would parse `/x` as positional under a naïve check); since CommandLine
    /// already accepts `--root` at any position alongside a subcommand, we treat ANY non-`-`
    /// arg as a subcommand-bearing invocation.
    /// </summary>
    internal static bool IsBare(string[] args)
    {
        if (args.Length == 0) return true;
        // The CommandLine.Parse contract is: args[0] is either the subcommand OR `-h/--help`.
        // Anything else (including known flags like `--root`) shows the user didn't pass a
        // subcommand. To compose with `--root <path>` style bare invocation, the first arg must
        // start with `-` AND the args must not contain anything that doesn't start with `-`
        // or a value following a flag we know wants one.
        return args[0].StartsWith('-');
    }
}

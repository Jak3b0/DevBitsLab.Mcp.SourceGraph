using DevBitsLab.Mcp.SourceGraph.Core;
using DevBitsLab.Mcp.SourceGraph.Storage;
using Spectre.Console;

namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Inline form rendered by the dashboard's Scopes detail view when the operator presses
/// <c>N</c>. Collects the three fields required to add a scope — <c>name</c>, <c>solution</c>
/// path, and an optional <c>isolated</c> flag — using straight Spectre prompts (the same
/// pattern <c>init</c>'s guided actions use), then delegates to
/// <see cref="Cli.ScopesCli.AddScopeToConfig"/> to validate and persist.
///
/// <para>
/// The form is rendered outside the dashboard's <c>Live</c> region (the caller drops out of
/// Live before invoking <see cref="Prompt"/>). Validation errors re-prompt for the offending
/// field rather than dismissing the form; <c>Esc</c> at the name prompt cancels the action
/// (returns <see cref="Result.Cancelled"/>).
/// </para>
/// </summary>
internal static class AddScopeForm
{
    /// <summary>Outcome of the form. Cancelled is distinct from Failure so the caller can suppress the toast.</summary>
    internal enum Outcome { Saved, Cancelled, Failed }

    /// <summary>Result carried back to the dispatcher: outcome plus a short message + the persisted scope id when applicable.</summary>
    internal sealed record Result(Outcome Outcome, string Message, string? ScopeId = null)
    {
        public static Result Cancelled => new(Outcome.Cancelled, "add scope cancelled", null);
    }

    /// <summary>
    /// Render the form against <paramref name="console"/>, persist the result via
    /// <see cref="Cli.ScopesCli.AddScopeToConfig"/>, and return the outcome. The caller is
    /// responsible for refreshing the dashboard snapshot on success.
    /// </summary>
    /// <param name="console">Spectre console for prompts; tests inject a <c>TestConsole</c>.</param>
    /// <param name="root">Repository root — the <c>.sourcegraph.json</c> location.</param>
    /// <param name="config">Current scope config snapshot; the form's validation runs against this.</param>
    public static Result Prompt(IAnsiConsole console, string root, ScopeConfig config)
    {
        console.WriteLine();
        console.MarkupLine($"[{DashboardTheme.MutedDim}]─── Add scope ───[/]");
        console.WriteLine();

        // 1. Name — must be a valid kebab-case scope id and not already taken.
        var name = console.Prompt(
            new TextPrompt<string>("[bold]Scope name:[/]")
                .Validate(candidate =>
                {
                    if (string.IsNullOrWhiteSpace(candidate))
                        return ValidationResult.Error("[red]name is required[/]");
                    if (!ScopeIdValidator.IsValid(candidate.Trim()))
                        return ValidationResult.Error("[red]must match ^[[a-z0-9]][[a-z0-9-]]{0,63}$ (kebab-case slug)[/]");
                    if (config.Scopes.Any(s => string.Equals(s.Id, candidate.Trim(), StringComparison.Ordinal)))
                        return ValidationResult.Error($"[red]scope '{Markup.Escape(candidate.Trim())}' already exists[/]");
                    return ValidationResult.Success();
                })
                .AllowEmpty()).Trim();
        if (string.IsNullOrEmpty(name)) return Result.Cancelled;

        // 2. Solution path — must resolve to an existing file after env-var expansion.
        // The inline form doesn't support glob patterns; users wanting glob-based scopes can
        // edit `.sourcegraph.json` directly via `[e]`. Keeping the prompt text honest.
        var solution = console.Prompt(
            new TextPrompt<string>("[bold]Solution path:[/]")
                .Validate(candidate =>
                {
                    if (string.IsNullOrWhiteSpace(candidate))
                        return ValidationResult.Error("[red]solution path is required[/]");
                    var expanded = ExpandPath(candidate.Trim(), root);
                    if (!File.Exists(expanded))
                        return ValidationResult.Error($"[red]file not found:[/] {Markup.Escape(expanded)}");
                    return ValidationResult.Success();
                })
                .AllowEmpty()).Trim();
        if (string.IsNullOrEmpty(solution)) return Result.Cancelled;

        // 3. Isolated — default No (most scopes participate in scope="*" fan-out).
        var isolated = console.Prompt(
            new ConfirmationPrompt("[bold]Isolated?[/] (excluded from scope=\"*\" fan-out)") { DefaultValue = false });

        // Persist via the shared core path. We pass the user-typed solution string (not the
        // expanded one) so the on-disk JSON keeps the same shape `scopes add --solution ...`
        // would emit; ScopesCli rebases absolute paths under <root> when possible.
        var save = Cli.ScopesCli.AddScopeToConfig(root, config, name, solution, isolated);
        if (!save.Ok)
        {
            return new Result(Outcome.Failed, save.Message);
        }
        return new Result(Outcome.Saved, $"added scope '{name}'", ScopeId: name);
    }

    /// <summary>
    /// Expand placeholder syntax in the input — the same vocabulary <c>CommandLine</c> accepts
    /// for <c>--solution</c> and friends, so the form's "file not found" validation accepts the
    /// same paths the user would type in <c>scopes add --solution</c>. Supported:
    /// <list type="bullet">
    /// <item><c>${workspaceFolder}</c> → <paramref name="root"/></item>
    /// <item><c>${HOME}</c> → user profile</item>
    /// <item><c>~/</c> prefix → user profile</item>
    /// <item><c>${VAR}</c> for any other env var, via <see cref="CommandLine.ExpandTokens"/></item>
    /// <item>relative path → resolved against <paramref name="root"/></item>
    /// </list>
    /// </summary>
    private static string ExpandPath(string raw, string root)
    {
        var expanded = raw;
        // ${workspaceFolder} → root (same fallback the CLI uses).
        expanded = expanded.Replace("${workspaceFolder}", root, StringComparison.Ordinal);
        // ${HOME} → user profile.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        expanded = expanded.Replace("${HOME}", home, StringComparison.Ordinal);
        // ~/ prefix.
        if (expanded.StartsWith("~/", StringComparison.Ordinal))
        {
            expanded = Path.Join(home, expanded[2..]);
        }
        // Any remaining ${VAR} → process env. Reuse CommandLine.ExpandTokens so the dashboard
        // and the CLI agree on the placeholder grammar. (The previous implementation called
        // `Environment.ExpandEnvironmentVariables`, which only expands the `%VAR%` Windows
        // form and silently passed `${FOO}` through unexpanded — the validation then said
        // "file not found" for a path the CLI's `--solution` would have happily resolved.)
        expanded = Cli.CommandLine.ExpandTokens(expanded);
        // Relative path → resolve against root.
        if (!Path.IsPathRooted(expanded))
        {
            expanded = Path.GetFullPath(Path.Join(root, expanded));
        }
        return expanded;
    }
}

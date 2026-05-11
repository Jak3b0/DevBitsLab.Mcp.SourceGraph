using Spectre.Console;

namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Modal confirmation prompt for destructive dashboard actions per design Decision 4. Renders
/// a Spectre <see cref="ConfirmationPrompt"/> with the text <c>&lt;verb&gt; &lt;target&gt;? [y/N]</c>,
/// default <c>No</c>, dismissable with <c>Esc</c> or <c>n</c>.
///
/// <para>
/// The dispatcher gates every destructive in-place action through this modal:
/// <see cref="DashboardAction.RebuildScope"/> (archive + cold-index),
/// <see cref="DashboardAction.UnwireClient"/> (removes the <c>sourcegraph</c> entry from a
/// client config), and <see cref="DashboardAction.RemoveScope"/> (removes a scope from
/// <c>.sourcegraph.json</c>; the on-disk per-scope DB is preserved as a re-add cache). All
/// other actions are read-only or idempotent and don't gate.
/// </para>
/// </summary>
internal static class ConfirmModal
{
    /// <summary>
    /// Show the prompt against <paramref name="console"/> and return the user's choice. Defaults
    /// to <c>false</c> on <c>Esc</c>/<c>Enter</c>/<c>n</c>; only an explicit <c>y</c>/<c>Y</c>
    /// returns true.
    /// </summary>
    public static bool Prompt(IAnsiConsole console, string verb, string target)
    {
        var prompt = new ConfirmationPrompt($"{verb} [yellow]{Markup.Escape(target)}[/]?")
        {
            DefaultValue = false,
            // ShowChoices = true,
            // ShowDefaultValue = true,
        };
        return console.Prompt(prompt);
    }
}

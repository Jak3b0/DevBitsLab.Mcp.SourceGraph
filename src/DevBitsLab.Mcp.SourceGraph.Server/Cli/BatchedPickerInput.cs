namespace DevBitsLab.Mcp.SourceGraph.Server.Cli;

/// <summary>
/// Outcome of parsing one batched-picker input line. <see cref="Selection"/> holds the resolved
/// slug set after applying the displayed defaults plus any <c>+</c>/<c>-</c> edits;
/// <see cref="UnknownSlugs"/> lists tokens that didn't match a known client (the caller emits a
/// warn for each); <see cref="NeedsReprompt"/> is true when the raw input was malformed (neither
/// empty / <c>y</c> / <c>n</c> nor a sequence of <c>+slug</c>/<c>-slug</c> tokens) — the picker
/// reprompts once and treats a second invalid input as <c>n</c>.
/// </summary>
internal sealed record PickerResult(
    IReadOnlySet<string> Selection,
    IReadOnlyList<string> UnknownSlugs,
    bool NeedsReprompt);

/// <summary>
/// Parses the single batched picker prompt input. Grammar:
/// <list type="bullet">
/// <item>Empty / <c>y</c> / <c>Y</c> — accept the displayed defaults verbatim.</item>
/// <item><c>n</c> / <c>N</c> — deselect every row.</item>
/// <item>Whitespace-separated <c>+slug</c> / <c>-slug</c> tokens — start from defaults; flip each
///   named client. Unknown slugs warn (collected into <see cref="PickerResult.UnknownSlugs"/>)
///   and are dropped from the selection delta.</item>
/// <item>Anything else — <see cref="PickerResult.NeedsReprompt"/> is true.</item>
/// </list>
/// </summary>
internal static class BatchedPickerInput
{
    public static PickerResult Parse(string raw, IReadOnlySet<string> defaults, IReadOnlySet<string> knownSlugs)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            return new PickerResult(
                Selection: new HashSet<string>(defaults, StringComparer.Ordinal),
                UnknownSlugs: Array.Empty<string>(),
                NeedsReprompt: false);
        }
        if (trimmed.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            return AllOff();
        }

        // Tokenise on whitespace; expect every token to start with `+` or `-`. Anything else
        // signals "reprompt".
        var tokens = trimmed.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var current = new HashSet<string>(defaults, StringComparer.Ordinal);
        var unknown = new List<string>();
        var sawValidEdit = false;

        foreach (var tok in tokens)
        {
            if (tok.Length < 2 || (tok[0] != '+' && tok[0] != '-'))
            {
                // Malformed token — kick the whole parse into reprompt mode.
                return new PickerResult(
                    Selection: defaults,
                    UnknownSlugs: Array.Empty<string>(),
                    NeedsReprompt: true);
            }
            var op = tok[0];
            var slug = tok.Substring(1);
            if (!knownSlugs.Contains(slug))
            {
                unknown.Add(slug);
                continue;
            }
            sawValidEdit = true;
            if (op == '+') current.Add(slug);
            else current.Remove(slug);
        }

        // If we got no recognisable edits at all and at least one unknown slug, still proceed
        // (per the spec's "unknown slug warns and is ignored" semantics) — the prompt is not
        // reprompted unless the *form* was wrong.
        _ = sawValidEdit; // local kept for clarity; intentional drop.

        return new PickerResult(
            Selection: current,
            UnknownSlugs: unknown,
            NeedsReprompt: false);
    }

    /// <summary>Helper returning the "deselect all" result used as the fallthrough on second
    /// invalid input.</summary>
    public static PickerResult AllOff() => new(
        Selection: new HashSet<string>(StringComparer.Ordinal),
        UnknownSlugs: Array.Empty<string>(),
        NeedsReprompt: false);
}

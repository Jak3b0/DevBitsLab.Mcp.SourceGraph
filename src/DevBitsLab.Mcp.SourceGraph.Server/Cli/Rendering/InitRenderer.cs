using DevBitsLab.Mcp.SourceGraph.Server.Cli.ClientConfigWriters;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;

namespace DevBitsLab.Mcp.SourceGraph.Server.Cli.Rendering;

/// <summary>
/// Phase-organised renderer for the <c>init</c> subcommand. One method per phase, every method
/// takes a <see cref="TextWriter"/> so tests can capture output without redirecting
/// <see cref="Console.Out"/>. The state-glyph language lives in <see cref="StateGlyph"/>; path
/// display lives in <see cref="PathDisplay"/>; this class composes the row layouts.
///
/// Five phases: <c>Environment</c>, <c>Clients to wire</c>, <c>Apply</c>, <c>Pre-warm</c>
/// (omitted when no pre-warm runs), <c>Next</c>. Phase headings sit at the left margin without
/// a glyph; row content under each heading is indented two spaces.
/// </summary>
internal static class InitRenderer
{
    // Column widths — kept as constants so future phase additions reuse the same alignment.
    private const string Indent = "  ";

    // 18-char column for the Environment phase key label, picked to accommodate
    // ".sourcegraph.json" (the longest label in today's detection summary).
    private const int EnvKeyWidth = 18;

    // 15-char column for slug labels (longest slug is "claude-desktop" at 14 chars).
    private const int SlugWidth = 15;

    // 18-char column for the Apply phase verb label so the slug column lines up.
    private const int VerbWidth = 18;

    /// <summary>
    /// Writes the banner line. The leading leaf is suppressed when
    /// <see cref="LeafFormatter.Suppressed"/> is true; everything else is unchanged.
    /// </summary>
    public static void RenderBanner(TextWriter writer, string? version = null)
    {
        var prefix = LeafFormatter.Suppressed ? "" : LeafFormatter.Mark;
        var versionTail = string.IsNullOrEmpty(version) ? "" : "    " + version;
        writer.WriteLine($"{prefix}SourceGraph init{versionTail}");
        writer.WriteLine();
    }

    /// <summary>
    /// Writes the <c>Environment</c> phase: SDK version, git availability, repo root, solutions
    /// detected, <c>.sourcegraph.json</c> status. Each row uses the state-glyph language
    /// (<see cref="StateGlyphKind.On"/> for present/pass, <see cref="StateGlyphKind.Warn"/> for
    /// soft warnings like missing git).
    /// </summary>
    public static void RenderEnvironment(TextWriter writer, OnboardingDetectionResult detection, string root, string? home)
    {
        writer.WriteLine("Environment");

        // .NET SDK
        WriteEnvRow(writer,
            kind: detection.DotnetSdkVersion is null ? StateGlyphKind.Warn : StateGlyphKind.On,
            key: ".NET SDK",
            value: detection.DotnetSdkVersion ?? "(not detected)");

        // git on PATH
        WriteEnvRow(writer,
            kind: detection.GitOnPath ? StateGlyphKind.On : StateGlyphKind.Warn,
            key: "git on PATH",
            value: detection.GitOnPath ? "yes" : "no (--no-history will be implied)");

        // repo root
        WriteEnvRow(writer,
            kind: StateGlyphKind.On,
            key: "repo root",
            value: PathDisplay.Render(detection.RepoRootPath, root, home));

        // solutions
        var solutionsRendered = detection.SolutionFiles.Count == 0
            ? "(none)"
            : string.Join(", ", detection.SolutionFiles.Select(p => PathDisplay.Render(p, root, home)));
        WriteEnvRow(writer,
            kind: detection.SolutionFiles.Count == 0 ? StateGlyphKind.Warn : StateGlyphKind.On,
            key: "solutions",
            value: solutionsRendered);

        // .sourcegraph.json
        var (sgKind, sgValue) = detection.SourceGraphConfigStatus switch
        {
            SourceGraphConfigStatus.Valid => (StateGlyphKind.On, "valid"),
            SourceGraphConfigStatus.Missing => (StateGlyphKind.On, "missing (single-scope synth path)"),
            SourceGraphConfigStatus.Malformed => (StateGlyphKind.Skip, $"MALFORMED — {detection.SourceGraphConfigError}"),
            _ => (StateGlyphKind.Warn, "?"),
        };
        WriteEnvRow(writer, sgKind, ".sourcegraph.json", sgValue);

        writer.WriteLine();
    }

    /// <summary>
    /// Writes the <c>Clients to wire</c> phase: one row per known client showing its
    /// default-selected state under the state-glyph language. Used to display the picker defaults
    /// before the batched prompt.
    /// </summary>
    public static void RenderClientsToWire(
        TextWriter writer,
        IReadOnlyList<ClientPickerRow> rows)
    {
        if (rows.Count == 0) return;
        writer.WriteLine("Clients to wire");
        foreach (var row in rows)
        {
            var kind = row.DefaultOn ? StateGlyphKind.On : StateGlyphKind.Off;
            var glyph = StateGlyph.For(kind);
            var slug = row.Slug.PadRight(SlugWidth);
            var scope = row.Scope.PadRight(8);
            writer.WriteLine($"{Indent}{glyph}{slug} {scope} {row.Detail}");
        }
        writer.WriteLine();
    }

    /// <summary>
    /// Writes the heading line for the <c>Apply</c> phase. Rows are appended one at a time by
    /// <see cref="RenderApplyRow"/> as each writer runs, so progress is visible mid-run instead
    /// of being buffered until the end.
    /// </summary>
    public static void RenderApplyHeading(TextWriter writer)
    {
        writer.WriteLine("Apply");
    }

    /// <summary>
    /// Writes a single Apply row for one <see cref="WriterAction"/> outcome plus its target slug
    /// and rendered path. The hanging description line is emitted indented +4 when the action is
    /// a skip or unsupported (so the user knows why), and elided otherwise.
    /// </summary>
    public static void RenderApplyRow(
        TextWriter writer,
        WriterAction action,
        string slug,
        string targetPath,
        string description,
        string root,
        string? home)
    {
        var (kind, verb) = action switch
        {
            WriterAction.Insert => (StateGlyphKind.On, "wrote"),
            WriterAction.ReplaceOurs => (StateGlyphKind.On, "replaced"),
            WriterAction.NoOpAlreadyMatches => (StateGlyphKind.On, "no change"),
            WriterAction.SkipExistingDiffers => (StateGlyphKind.Skip, "conflict — skipped"),
            WriterAction.SkipHasComments => (StateGlyphKind.Warn, "skipped — comments"),
            WriterAction.SkipUnsupported => (StateGlyphKind.Unsupported, "skipped — unsupported"),
            _ => (StateGlyphKind.Warn, "?"),
        };

        var glyph = StateGlyph.For(kind);
        var displayPath = PathDisplay.Render(targetPath, root, home);
        var verbPadded = verb.PadRight(VerbWidth);
        var slugPadded = slug.PadRight(SlugWidth);
        writer.WriteLine($"{Indent}{glyph}{verbPadded} {slugPadded} {displayPath}");

        // Hanging detail line for conflict / unsupported / comments — these are the cases where
        // the user needs to read why nothing got written.
        if (!string.IsNullOrEmpty(description) &&
            action is WriterAction.SkipExistingDiffers
                or WriterAction.SkipUnsupported
                or WriterAction.SkipHasComments)
        {
            writer.WriteLine($"{Indent}    {description}");
        }
    }

    /// <summary>
    /// Writes the <c>Pre-warm</c> phase heading + summary line. On success, the summary is a
    /// single positive row naming the solution and elapsed time; on non-zero exit the row
    /// degrades to a warning. The child indexer's stdout (already inherited at the
    /// <see cref="System.Diagnostics.Process"/> layer) appears between the heading and this
    /// summary in real time.
    /// </summary>
    public static void RenderPreWarmSummary(
        TextWriter writer,
        int exitCode,
        TimeSpan elapsed,
        string solutionName)
    {
        if (exitCode == 0)
        {
            var glyph = StateGlyph.For(StateGlyphKind.On);
            writer.WriteLine($"{Indent}{glyph}indexed {solutionName} in {elapsed.TotalSeconds:F1}s");
        }
        else
        {
            var glyph = StateGlyph.For(StateGlyphKind.Warn);
            writer.WriteLine($"{Indent}{glyph}pre-warm exit {exitCode} after {elapsed.TotalSeconds:F1}s");
        }
        writer.WriteLine();
    }

    /// <summary>
    /// Writes the heading for the <c>Pre-warm</c> phase. Caller is expected to emit indexer
    /// output between this and the matching <see cref="RenderPreWarmSummary"/>.
    /// </summary>
    public static void RenderPreWarmHeading(TextWriter writer, string solutionName)
    {
        writer.WriteLine("Pre-warm");
        writer.WriteLine($"{Indent}pre-warming against {solutionName}…");
    }

    /// <summary>
    /// Writes the <c>Next</c> phase: prose suggestions for what to do after init completes.
    /// </summary>
    public static void RenderNext(TextWriter writer, IReadOnlyList<string> suggestions)
    {
        if (suggestions.Count == 0) return;
        writer.WriteLine();
        writer.WriteLine("Next");
        foreach (var s in suggestions)
        {
            writer.WriteLine($"{Indent}{s}");
        }
    }

    private static void WriteEnvRow(TextWriter writer, StateGlyphKind kind, string key, string value)
    {
        var glyph = StateGlyph.For(kind);
        var keyPadded = key.PadRight(EnvKeyWidth);
        writer.WriteLine($"{Indent}{glyph}{keyPadded} {value}");
    }
}

/// <summary>
/// One row in the picker display. Slug matches <see cref="ClientId.ToSlug"/>; scope is
/// <c>"project"</c> or <c>"user"</c>; detail is free-text appended at the end of the row.
/// </summary>
internal sealed record ClientPickerRow(
    string Slug,
    bool DefaultOn,
    string Scope,
    string Detail);

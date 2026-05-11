using Spectre.Console;

namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Composes the home + detail-view Spectre <see cref="Layout"/> for the dashboard. The minimum
/// terminal dimensions are <c>80 × 24</c>; smaller terminals get a single panel announcing the
/// limit per the spec's "Tiny terminal refuses to render" scenario.
///
/// <para>
/// The layout is intentionally simple: a fixed header region, a flexible body region (the active
/// view's renderer fills this), and a fixed footer region. The body is repurposed per view —
/// home view fills it with the summary block + menu; detail views fill it with a full-width
/// table + selected drawer.
/// </para>
/// </summary>
internal static class DashboardLayout
{
    public const int MinimumWidth = 80;
    public const int MinimumHeight = 24;

    /// <summary>Region names — keep in sync with the layout assembled in <see cref="Build"/>.</summary>
    public const string HeaderRegion = "header";
    /// <summary>The single body region. Every view (home + each detail) writes here.</summary>
    public const string BodyRegion = "body";
    public const string FooterRegion = "footer";

    /// <summary>
    /// Build the layout. Returns either the three-region composition (header / body / footer) or
    /// a one-cell "too small" panel when the dimensions don't meet the minimum.
    /// </summary>
    public static Layout Build(int width, int height)
    {
        if (width < MinimumWidth || height < MinimumHeight)
        {
            return new Layout("root").Update(BuildTooSmall(width, height));
        }

        // Vertical composition:
        //   header (3 rows: brand line + separator + spacer)
        //   body   (flex — the active view writes here)
        //   footer (5 rows: separator + hint row + spacer + toast)
        var root = new Layout("root").SplitRows(
            new Layout(HeaderRegion).Size(3),
            new Layout(BodyRegion),
            new Layout(FooterRegion).Size(5));
        return root;
    }

    private static Panel BuildTooSmall(int width, int height)
    {
        var msg = $"terminal too small (need ≥{MinimumWidth}×{MinimumHeight}; got {width}×{height})";
        return new Panel(new Markup($"[red]{Markup.Escape(msg)}[/]"))
        {
            Border = BoxBorder.Rounded,
            Padding = new Padding(2, 1, 2, 1),
        };
    }
}

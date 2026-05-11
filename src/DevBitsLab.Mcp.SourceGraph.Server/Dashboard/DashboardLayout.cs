using Spectre.Console;

namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Composes the five-section Spectre <see cref="Layout"/> for the dashboard. The minimum
/// terminal dimensions are <c>80 × 24</c>; smaller terminals get a single panel announcing the
/// limit per the spec's "Tiny terminal refuses to render" scenario.
///
/// <para>
/// Region names map to the five snapshot surfaces plus the header + footer regions:
/// <c>header</c>, <c>environment</c>, <c>scopes</c>, <c>clients</c>, <c>embeddings</c>,
/// <c>recent</c>, <c>footer</c>. The main loop targets each by name when it updates one
/// section's renderable independently of the others.
/// </para>
/// </summary>
internal static class DashboardLayout
{
    public const int MinimumWidth = 80;
    public const int MinimumHeight = 24;

    /// <summary>Region names — keep in sync with the layout assembled in <see cref="Build"/>.</summary>
    public const string HeaderRegion = "header";
    public const string EnvironmentRegion = "environment";
    public const string ScopesRegion = "scopes";
    public const string ClientsRegion = "clients";
    public const string EmbeddingsRegion = "embeddings";
    public const string RecentRegion = "recent";
    public const string FooterRegion = "footer";

    /// <summary>
    /// Build the layout. Returns either the five-section composition or a one-cell "too small"
    /// panel when the dimensions don't meet the minimum.
    /// </summary>
    public static Layout Build(int width, int height)
    {
        if (width < MinimumWidth || height < MinimumHeight)
        {
            return new Layout("root").Update(BuildTooSmall(width, height));
        }

        // Vertical composition:
        //   header (3 rows)
        //   top row split horizontally: Environment + Scopes
        //   middle row split horizontally: Clients + Embeddings
        //   recent activity (flex)
        //   footer (3 rows)
        var root = new Layout("root").SplitRows(
            new Layout(HeaderRegion).Size(3),
            new Layout("top").Size(8).SplitColumns(
                new Layout(EnvironmentRegion),
                new Layout(ScopesRegion)),
            new Layout("middle").Size(8).SplitColumns(
                new Layout(ClientsRegion),
                new Layout(EmbeddingsRegion)),
            new Layout(RecentRegion),
            new Layout(FooterRegion).Size(3));
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

    /// <summary>Map a <see cref="DashboardSection"/> to its layout region name.</summary>
    public static string RegionFor(DashboardSection section) => section switch
    {
        DashboardSection.Environment => EnvironmentRegion,
        DashboardSection.Scopes => ScopesRegion,
        DashboardSection.Clients => ClientsRegion,
        DashboardSection.Embeddings => EmbeddingsRegion,
        DashboardSection.RecentActivity => RecentRegion,
        _ => throw new ArgumentOutOfRangeException(nameof(section), section, "unknown section"),
    };
}

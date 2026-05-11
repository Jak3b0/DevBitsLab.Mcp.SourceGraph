namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// The five sections the dashboard renders in order. Identifies the focused section for
/// navigation; row selection within a section is tracked separately in the dashboard state.
/// </summary>
internal enum DashboardSection
{
    Environment,
    Scopes,
    Clients,
    Embeddings,
    RecentActivity,
}

/// <summary>
/// The dashboard's top-level view state. Distinct from <see cref="DashboardSection"/>: a view is
/// what the renderer is currently filling the body region with; a section identifies a per-area
/// surface in the snapshot. <see cref="Home"/> has no matching section — it's the
/// welcome/landing view with the menu + at-a-glance summary block. Every other view has a 1:1
/// mapping to a <see cref="DashboardSection"/>.
/// </summary>
internal enum DashboardView
{
    /// <summary>Welcome / landing view: summary block + numeric menu.</summary>
    Home,
    /// <summary>Scopes detail: full per-scope table + selected-scope drawer.</summary>
    Scopes,
    /// <summary>Clients detail: full per-client table + selected-client drawer.</summary>
    Clients,
    /// <summary>Embeddings detail: model id / cache / size / verified flag.</summary>
    Embeddings,
    /// <summary>Recent activity detail: scrollable log of tool calls + heals.</summary>
    RecentActivity,
    /// <summary>Environment detail: SDK / git / repo root / solutions / config (read-only).</summary>
    Environment,
}

/// <summary>Helpers for mapping between <see cref="DashboardView"/> and <see cref="DashboardSection"/>.</summary>
internal static class DashboardViewExtensions
{
    /// <summary>
    /// Map a detail view to its corresponding <see cref="DashboardSection"/>. Returns null for
    /// <see cref="DashboardView.Home"/> (which has no underlying section surface).
    /// </summary>
    public static DashboardSection? ToSection(this DashboardView view) => view switch
    {
        DashboardView.Scopes => DashboardSection.Scopes,
        DashboardView.Clients => DashboardSection.Clients,
        DashboardView.Embeddings => DashboardSection.Embeddings,
        DashboardView.RecentActivity => DashboardSection.RecentActivity,
        DashboardView.Environment => DashboardSection.Environment,
        _ => null,
    };

    /// <summary>Human-readable section name as it appears in detail-view breadcrumbs.</summary>
    public static string DisplayName(this DashboardView view) => view switch
    {
        DashboardView.Home => "Home",
        DashboardView.Scopes => "Scopes",
        DashboardView.Clients => "Clients",
        DashboardView.Embeddings => "Embeddings",
        DashboardView.RecentActivity => "Recent activity",
        DashboardView.Environment => "Environment",
        _ => view.ToString(),
    };
}

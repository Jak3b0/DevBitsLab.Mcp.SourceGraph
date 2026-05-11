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

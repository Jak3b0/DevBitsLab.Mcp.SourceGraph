using System.Reflection;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using DevBitsLab.Mcp.SourceGraph.Server.Cli.Snapshot;
using DevBitsLab.Mcp.SourceGraph.Server.Tools;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DevBitsLab.Mcp.SourceGraph.Server.Dashboard;

/// <summary>
/// Entry point for the <c>sourcegraph-mcp dashboard</c> subcommand. Composes
/// <see cref="DashboardLayout"/> + <see cref="DashboardRenderer"/> + <see cref="FreshnessSource"/>
/// + <see cref="DashboardActions"/> into a live operator console.
///
/// <para>
/// The main loop runs three concurrent activities: (1) snapshot rebuilds from
/// <see cref="FreshnessSource"/>, (2) keystroke polling from <see cref="Console.ReadKey"/>,
/// (3) Spectre's <see cref="LiveDisplay"/> re-rendering on snapshot change. They communicate
/// via a single <see cref="System.Threading.Channels.Channel{T}"/>-of-actions on the UI thread
/// so the renderer is the only writer to the terminal.
/// </para>
/// </summary>
internal static class DashboardCli
{
    /// <summary>
    /// Run the dashboard loop. Returns exit code 0 on quit / Ctrl+C, exit code 2 when the
    /// terminal is below the minimum dimensions, exit code 1 on unexpected exception (after
    /// restoring the cursor).
    /// </summary>
    public static async Task<int> RunAsync(CommandLine cli, CancellationToken token = default)
    {
        var root = cli.ResolvedRepoRoot();

        // Apply --no-leaf early so glyph rendering reflects the user's preference for the
        // whole session (matches the same chokepoint Program.cs uses for the serve path).
        if (cli.NoLeaf || string.Equals(Environment.GetEnvironmentVariable(LeafFormatter.EnvVarName), "1", StringComparison.Ordinal))
        {
            LeafFormatter.Suppressed = true;
        }

        // Minimum dimensions probe per spec scenario "Tiny terminal refuses to render".
        int width, height;
        try
        {
            width = Console.WindowWidth;
            height = Console.WindowHeight;
        }
        catch (IOException)
        {
            // No tty — operator probably wanted `status` but invoked `dashboard` explicitly.
            // The bare-command dispatch should have caught this earlier; defensive belt here.
            await Console.Error.WriteLineAsync("dashboard requires a tty; use `sourcegraph-mcp status` for redirected stdout").ConfigureAwait(false);
            return 2;
        }
        if (width < DashboardLayout.MinimumWidth || height < DashboardLayout.MinimumHeight)
        {
            await Console.Error.WriteLineAsync(
                $"terminal too small (need ≥{DashboardLayout.MinimumWidth}×{DashboardLayout.MinimumHeight})").ConfigureAwait(false);
            return 2;
        }

        // Honour --no-color by configuring Spectre's console; the env-var NO_COLOR is honoured
        // by Spectre natively.
        var console = AnsiConsole.Console;
        if (cli.NoColor)
        {
            console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                ColorSystem = ColorSystemSupport.NoColors,
                Ansi = AnsiSupport.No,
            });
        }

        var options = new SnapshotOptions(
            ActivityBytes: cli.ActivityBytes ?? 524288,
            RecentActivityCap: 50);
        using var freshness = new FreshnessSource(root, options);

        var loop = new LoopState(console, freshness, root, GetVersion());
        freshness.SnapshotChanged += loop.OnSnapshotReady;
        freshness.Start();

        // Wait briefly for the first snapshot so the initial draw isn't a blank skeleton; if
        // BuildAsync takes longer than 2 s we proceed with no snapshot and the placeholder
        // panels render.
        for (var i = 0; i < 20 && loop.Snapshot is null; i++)
        {
            await Task.Delay(100, token).ConfigureAwait(false);
        }

        try
        {
            await loop.RunAsync(token).ConfigureAwait(false);
            return loop.ExitCode;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception ex)
        {
            // Restore cursor before surfacing the error.
            try { Console.CursorVisible = true; } catch { }
            await Console.Error.WriteLineAsync($"dashboard error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Cleaned version string for the header bar. Reads the assembly's
    /// <see cref="AssemblyInformationalVersionAttribute"/> (SourceLink stamps it) and falls back
    /// to <see cref="AssemblyName.Version"/>.
    /// </summary>
    private static string GetVersion()
    {
        var asm = typeof(DashboardCli).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Encapsulates the per-frame state of the main loop: current snapshot, selection,
    /// help-overlay flag, status message, exit code. Keeping it as an instance class (vs. a
    /// pile of locals in <see cref="RunAsync"/>) lets the snapshot-changed event handler and
    /// the key-handler share state without a tangle of captured locals.
    /// </summary>
    private sealed class LoopState
    {
        private readonly IAnsiConsole _console;
        private readonly FreshnessSource _freshness;
        private readonly DashboardRenderOptions _renderOptions;
        private readonly Lock _gate = new();
        private DashboardSnapshot? _snapshot;
        private DashboardSection _focused = DashboardSection.Scopes;
        private int _selectedRow;
        private bool _helpOpen;
        private string _statusMessage = "";
        private bool _dirty = true;

        public LoopState(IAnsiConsole console, FreshnessSource freshness, string root, string version)
        {
            _console = console;
            _freshness = freshness;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolderOption.DoNotVerify);
            _renderOptions = new DashboardRenderOptions(Root: root, Home: home, NoColor: false, Version: version);
        }

        public int ExitCode { get; private set; }
        public DashboardSnapshot? Snapshot { get { lock (_gate) return _snapshot; } }

        public void OnSnapshotReady(DashboardSnapshot snapshot)
        {
            lock (_gate)
            {
                _snapshot = snapshot;
                _dirty = true;
            }
        }

        public async Task RunAsync(CancellationToken token)
        {
            var layout = DashboardLayout.Build(Console.WindowWidth, Console.WindowHeight);
            await _console.Live(layout).StartAsync(async ctx =>
            {
                // Tight loop: poll for keys with a short timeout, redraw on every snapshot
                // change or selection change, exit on Quit.
                while (!token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (!DashboardKeyMap.TryResolve(key, out var action))
                            continue;
                        var stayInLive = HandleNavigation(action);
                        if (action == DashboardAction.Quit)
                        {
                            ExitCode = 0;
                            break;
                        }
                        if (!stayInLive)
                        {
                            // Guided action: drop out of Live, run subprocess, re-enter on the
                            // next outer iteration. Spectre re-enters Live transparently when
                            // we redraw.
                            await DispatchActionAsync(action, token).ConfigureAwait(false);
                            MarkDirty();
                        }
                        else
                        {
                            await DispatchActionAsync(action, token).ConfigureAwait(false);
                            MarkDirty();
                        }
                    }
                    if (IsDirty())
                    {
                        Redraw(layout);
                        ctx.Refresh();
                    }
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Resolve navigation-only actions (up/down/tab/help) without going through the
        /// dispatcher. Returns false for guided actions so the caller knows to drop out of
        /// the Live region before spawning the subprocess.
        /// </summary>
        private bool HandleNavigation(DashboardAction action)
        {
            switch (action)
            {
                case DashboardAction.MoveUp:
                    MoveSelection(-1);
                    return true;
                case DashboardAction.MoveDown:
                    MoveSelection(+1);
                    return true;
                case DashboardAction.NextSection:
                    ChangeSection(+1);
                    return true;
                case DashboardAction.PreviousSection:
                    ChangeSection(-1);
                    return true;
                case DashboardAction.ToggleHelp:
                    lock (_gate) _helpOpen = !_helpOpen;
                    MarkDirty();
                    return true;
                case DashboardAction.CloseDetail:
                    lock (_gate) _helpOpen = false;
                    MarkDirty();
                    return true;
                case DashboardAction.InitGuided:
                case DashboardAction.DemoGuided:
                case DashboardAction.OpenLogInPager:
                case DashboardAction.OpenConfigInEditor:
                    return false; // guided: must suspend Live
                default:
                    return true;
            }
        }

        private async Task DispatchActionAsync(DashboardAction action, CancellationToken token)
        {
            DashboardSnapshot? snap;
            DashboardSelection sel;
            lock (_gate)
            {
                snap = _snapshot;
                sel = new DashboardSelection(_focused, _selectedRow);
            }
            if (snap is null) return;
            var ctx = new DashboardActionContext(
                Snapshot: snap,
                Selection: sel,
                Console: _console,
                Freshness: _freshness,
                Root: _renderOptions.Root,
                Policy: DashboardActionPolicy.Default);
            var result = await DashboardActions.RunAsync(action, ctx, token).ConfigureAwait(false);
            if (result.Quit) ExitCode = 0;
            lock (_gate)
            {
                _statusMessage = result.Message;
            }
        }

        private void MoveSelection(int delta)
        {
            lock (_gate)
            {
                if (_snapshot is null) return;
                var count = CountRowsIn(_focused, _snapshot);
                if (count <= 0) return;
                _selectedRow = ((_selectedRow + delta) % count + count) % count;
                _dirty = true;
            }
        }

        private void ChangeSection(int delta)
        {
            lock (_gate)
            {
                var values = Enum.GetValues<DashboardSection>();
                var idx = Array.IndexOf(values, _focused);
                idx = ((idx + delta) % values.Length + values.Length) % values.Length;
                _focused = values[idx];
                _selectedRow = 0;
                _dirty = true;
            }
        }

        private static int CountRowsIn(DashboardSection section, DashboardSnapshot snapshot) => section switch
        {
            DashboardSection.Environment => 5, // five rows but not row-selectable in v1
            DashboardSection.Scopes => snapshot.Scopes.Count,
            DashboardSection.Clients => snapshot.Clients.Count,
            DashboardSection.Embeddings => 1,
            DashboardSection.RecentActivity => snapshot.RecentActivity.Count,
            _ => 0,
        };

        private void MarkDirty() { lock (_gate) _dirty = true; }
        private bool IsDirty() { lock (_gate) { var d = _dirty; _dirty = false; return d; } }

        private void Redraw(Layout layout)
        {
            DashboardSnapshot? snap;
            DashboardSection focused;
            int selectedRow;
            bool helpOpen;
            string status;
            lock (_gate)
            {
                snap = _snapshot;
                focused = _focused;
                selectedRow = _selectedRow;
                helpOpen = _helpOpen;
                status = _statusMessage;
            }

            if (snap is null)
            {
                layout[DashboardLayout.HeaderRegion].Update(new Panel(new Markup("[grey]Loading snapshot…[/]")) { Border = BoxBorder.None });
                return;
            }

            layout[DashboardLayout.HeaderRegion].Update(DashboardRenderer.BuildHeader(snap, _renderOptions));
            layout[DashboardLayout.EnvironmentRegion].Update(DashboardRenderer.BuildEnvironmentSection(snap, _renderOptions));
            layout[DashboardLayout.ScopesRegion].Update(DashboardRenderer.BuildScopesSection(snap, _renderOptions,
                focused: focused == DashboardSection.Scopes, selectedRow: selectedRow));
            layout[DashboardLayout.ClientsRegion].Update(DashboardRenderer.BuildClientsSection(snap, _renderOptions,
                focused: focused == DashboardSection.Clients, selectedRow: selectedRow));
            layout[DashboardLayout.EmbeddingsRegion].Update(DashboardRenderer.BuildEmbeddingsSection(snap, _renderOptions,
                focused: focused == DashboardSection.Embeddings));
            layout[DashboardLayout.RecentRegion].Update(DashboardRenderer.BuildRecentActivitySection(snap, _renderOptions,
                focused: focused == DashboardSection.RecentActivity, selectedRow: selectedRow));
            var footer = helpOpen
                ? RenderHelpFooter()
                : DashboardRenderer.BuildFooter(status);
            layout[DashboardLayout.FooterRegion].Update(footer);
        }

        private static IRenderable RenderHelpFooter()
        {
            return new Panel(new Markup($"[grey]{Markup.Escape(DashboardKeyMap.HelpText)}[/]"))
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("[bold]Key reference (press `?` again to close)[/]"),
                Padding = new Padding(1, 0, 1, 0),
            };
        }
    }
}

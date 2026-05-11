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
            RecentActivityCap: 50,
            ModelId: cli.Model);
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
    /// Toast state: text + when it was set + severity. The renderer fades it out as wall-clock
    /// time advances; an empty <see cref="Text"/> hides the toast entirely.
    /// </summary>
    internal readonly record struct ToastState(string Text, DateTimeOffset SetAt, ToastSeverity Severity);

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
        private ToastState _toast = new("", DateTimeOffset.MinValue, ToastSeverity.Info);
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
            // The Live block owns the terminal's alternate region while it's active. Guided
            // actions (`init`, `demo`, `$PAGER`, `$EDITOR`) inherit stdio and write directly to
            // the same terminal — running them inside the Live callback interleaves their
            // output with Spectre's cursor-positioning escapes and leaves the terminal in a
            // broken state.
            //
            // The outer loop here handles that explicitly: each Live session runs until the
            // user quits, a guided action is picked, or the token cancels. On a guided action
            // we exit the Live callback, run the subprocess against a "clean" terminal, then
            // re-enter Live for the next session.
            while (!token.IsCancellationRequested)
            {
                var layout = DashboardLayout.Build(Console.WindowWidth, Console.WindowHeight);
                DashboardAction? pendingGuided = null;
                var quit = false;

                await _console.Live(layout).StartAsync(async ctx =>
                {
                    // Tight loop: poll for keys with a short timeout, redraw on every snapshot
                    // change or selection change, exit on Quit or a guided action.
                    while (!token.IsCancellationRequested)
                    {
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(intercept: true);
                            if (!DashboardKeyMap.TryResolve(key, out var action))
                                continue;
                            if (action == DashboardAction.Quit)
                            {
                                ExitCode = 0;
                                quit = true;
                                return;
                            }
                            var stayInLive = HandleNavigation(action);
                            if (!stayInLive)
                            {
                                // Guided action: leave Live so the subprocess has the terminal
                                // to itself. The outer loop runs it and re-enters Live afterwards.
                                pendingGuided = action;
                                return;
                            }
                            await DispatchActionAsync(action, token).ConfigureAwait(false);
                            MarkDirty();
                        }
                        if (IsDirty())
                        {
                            Redraw(layout);
                            ctx.Refresh();
                        }
                        await Task.Delay(50, token).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);

                if (quit || token.IsCancellationRequested) break;

                if (pendingGuided.HasValue)
                {
                    // Live is now stopped; the subprocess sees a normal terminal. After it
                    // returns we mark the snapshot dirty so the next Live iteration redraws
                    // immediately rather than showing the stale frame.
                    await DispatchActionAsync(pendingGuided.Value, token).ConfigureAwait(false);
                    MarkDirty();
                }
            }
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

            // PrimaryAction (Enter) is section-specific: Scopes → reindex, Clients → wire/unwire
            // toggle, Embeddings → pull, RecentActivity → surface a discoverability hint. Resolve
            // here before handing to the dispatcher so the dispatcher's per-action validation
            // (e.g. "reindex requires Scopes section selected") fires under the right action.
            if (action == DashboardAction.PrimaryAction)
            {
                action = DashboardPrimaryAction.Resolve(sel, snap);
            }

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
                _toast = string.IsNullOrEmpty(result.Message)
                    ? new ToastState("", DateTimeOffset.MinValue, ToastSeverity.Info)
                    : new ToastState(result.Message, DateTimeOffset.UtcNow, result.Severity);
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
                // Environment is always-unfocused (read-only, never enters the Tab cycle); the
                // user reported in bug-1 that Tab landing on Environment looked broken because
                // BuildEnvironmentSection didn't take a focused parameter. We skip it here so
                // the Tab cycle is { Scopes, Clients, Embeddings, RecentActivity } only.
                var cycle = new[]
                {
                    DashboardSection.Scopes,
                    DashboardSection.Clients,
                    DashboardSection.Embeddings,
                    DashboardSection.RecentActivity,
                };
                var idx = Array.IndexOf(cycle, _focused);
                if (idx < 0) idx = 0; // defensive: if _focused was somehow Environment, land on Scopes
                idx = ((idx + delta) % cycle.Length + cycle.Length) % cycle.Length;
                _focused = cycle[idx];
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
        private bool IsDirty()
        {
            lock (_gate)
            {
                // Force a redraw while a toast is visible so its fade-out animation actually plays
                // (the renderer reads the toast's age relative to now to fade it out; without
                // this, no input + no snapshot change = no redraw = stuck-looking toast).
                if (_toast.Text.Length > 0)
                {
                    var age = DateTimeOffset.UtcNow - _toast.SetAt;
                    if (age < TimeSpan.FromSeconds(5)) _dirty = true;
                    else _toast = new ToastState("", DateTimeOffset.MinValue, ToastSeverity.Info);
                }
                var d = _dirty;
                _dirty = false;
                return d;
            }
        }

        private void Redraw(Layout layout)
        {
            DashboardSnapshot? snap;
            DashboardSection focused;
            int selectedRow;
            bool helpOpen;
            ToastState toast;
            lock (_gate)
            {
                snap = _snapshot;
                focused = _focused;
                selectedRow = _selectedRow;
                helpOpen = _helpOpen;
                toast = _toast;
            }

            if (snap is null)
            {
                layout[DashboardLayout.HeaderRegion].Update(new Panel(new Markup("[grey]Loading snapshot…[/]")) { Border = BoxBorder.None });
                return;
            }

            layout[DashboardLayout.HeaderRegion].Update(DashboardRenderer.BuildHeader(snap, _renderOptions));
            // Environment is never in the Tab cycle — render always-unfocused.
            layout[DashboardLayout.EnvironmentRegion].Update(DashboardRenderer.BuildEnvironmentSection(snap, _renderOptions, focused: false));
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
                : DashboardRenderer.BuildFooter(toast);
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

/// <summary>
/// Routes the <c>Enter</c> (PrimaryAction) key to a concrete action based on which section is
/// focused. Lifted out of <see cref="DashboardCli"/>'s private LoopState so unit tests can
/// pin the wire/unwire toggle behaviour the spec calls out without standing up the full loop.
/// </summary>
internal static class DashboardPrimaryAction
{
    /// <summary>
    /// Map <c>Enter</c> (PrimaryAction) to the section-specific concrete action.
    /// <list type="bullet">
    /// <item><see cref="DashboardSection.Scopes"/> → reindex the selected scope.</item>
    /// <item><see cref="DashboardSection.Clients"/> → toggle wire/unwire on the selected client (the wire/unwire affordance the spec calls out for the Clients section).</item>
    /// <item><see cref="DashboardSection.Embeddings"/> → pull the active model.</item>
    /// <item><see cref="DashboardSection.RecentActivity"/> → no primary action yet (return <see cref="DashboardAction.None"/>; the dispatcher surfaces a discoverability hint).</item>
    /// <item><see cref="DashboardSection.Environment"/> → no primary action (Environment is never in the focus cycle).</item>
    /// </list>
    /// </summary>
    public static DashboardAction Resolve(DashboardSelection sel, DashboardSnapshot snap) =>
        sel.FocusedSection switch
        {
            DashboardSection.Scopes => DashboardAction.ReindexScope,
            DashboardSection.Clients => ResolveClientToggle(sel, snap),
            DashboardSection.Embeddings => DashboardAction.EmbeddingsPull,
            DashboardSection.RecentActivity => DashboardAction.None,
            DashboardSection.Environment => DashboardAction.None,
            _ => DashboardAction.None,
        };

    private static DashboardAction ResolveClientToggle(DashboardSelection sel, DashboardSnapshot snap)
    {
        // Mirror DashboardActions.OrderedClients ordering so the same row index resolves.
        var clients = snap.Clients.OrderBy(c => c.Scope == "project" ? 0 : 1).ToArray();
        if (sel.RowIndex < 0 || sel.RowIndex >= clients.Length) return DashboardAction.WireClient;
        return clients[sel.RowIndex].ContainsSourcegraphEntry
            ? DashboardAction.UnwireClient
            : DashboardAction.WireClient;
    }
}

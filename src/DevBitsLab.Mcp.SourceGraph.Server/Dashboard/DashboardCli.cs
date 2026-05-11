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
///
/// <para>
/// View model: the dashboard has a <see cref="DashboardView.Home"/> landing view (summary block
/// + numeric menu) and five detail views (Scopes, Clients, Embeddings, Recent activity,
/// Environment). The user navigates from home into a detail view via Enter (on the highlighted
/// menu item) or number keys 1–5, and returns to home with Esc or 'h'.
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
    /// Encapsulates the per-frame state of the main loop: current snapshot, view, selection,
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
        private DashboardView _currentView = DashboardView.Home;
        private int _homeMenuIndex;
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
        /// Resolve navigation-only actions (up/down/help) without going through the
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
                case DashboardAction.ToggleHelp:
                    lock (_gate) _helpOpen = !_helpOpen;
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
            DashboardView currentView;
            int homeMenuIndex;
            int selectedRow;
            lock (_gate)
            {
                snap = _snapshot;
                currentView = _currentView;
                homeMenuIndex = _homeMenuIndex;
                selectedRow = _selectedRow;
            }
            if (snap is null) return;

            // ──────────────────────────────────────────────────────────────────────
            // View transitions (no work for the action dispatcher to do)
            // ──────────────────────────────────────────────────────────────────────
            var target = TargetViewFor(action);
            if (target.HasValue)
            {
                SwitchView(target.Value);
                return;
            }

            // PrimaryAction (Enter) is view-dependent:
            //   - Home → open the highlighted menu item's view
            //   - Detail → dispatch the section's primary action
            if (action == DashboardAction.PrimaryAction)
            {
                if (currentView == DashboardView.Home)
                {
                    var entries = DashboardRenderer.HomeMenuEntries;
                    if (homeMenuIndex >= 0 && homeMenuIndex < entries.Length)
                    {
                        SwitchView(entries[homeMenuIndex].View);
                    }
                    return;
                }
                action = DashboardPrimaryAction.ResolveForView(currentView, selectedRow, snap);
                if (action == DashboardAction.None)
                {
                    // Surface a discoverability hint instead of hanging silently.
                    lock (_gate)
                    {
                        _toast = new ToastState(
                            "no primary action for this view",
                            DateTimeOffset.UtcNow, ToastSeverity.Info);
                        _dirty = true;
                    }
                    return;
                }
            }

            // Section-specific action keys only resolve in their owning view; gate silently.
            if (!IsActionAllowedInView(action, currentView))
            {
                return;
            }

            var section = currentView.ToSection() ?? DashboardSection.Scopes;
            var sel = new DashboardSelection(section, selectedRow);

            var actionCtx = new DashboardActionContext(
                Snapshot: snap,
                Selection: sel,
                Console: _console,
                Freshness: _freshness,
                Root: _renderOptions.Root,
                Policy: DashboardActionPolicy.Default);
            var result = await DashboardActions.RunAsync(action, actionCtx, token).ConfigureAwait(false);
            if (result.Quit) ExitCode = 0;
            lock (_gate)
            {
                _toast = string.IsNullOrEmpty(result.Message)
                    ? new ToastState("", DateTimeOffset.MinValue, ToastSeverity.Info)
                    : new ToastState(result.Message, DateTimeOffset.UtcNow, result.Severity);
            }
        }

        private static bool IsActionAllowedInView(DashboardAction action, DashboardView view) =>
            DashboardViewGating.IsAllowed(action, view);

        /// <summary>
        /// Map a view-transition action (<see cref="DashboardAction.GoHome"/>,
        /// <see cref="DashboardAction.OpenScopes"/>, …) to the view it lands on. Returns null
        /// for actions that aren't view transitions.
        /// </summary>
        private static DashboardView? TargetViewFor(DashboardAction action) => action switch
        {
            DashboardAction.GoHome => DashboardView.Home,
            DashboardAction.OpenScopes => DashboardView.Scopes,
            DashboardAction.OpenClients => DashboardView.Clients,
            DashboardAction.OpenEmbeddings => DashboardView.Embeddings,
            DashboardAction.OpenRecentActivity => DashboardView.RecentActivity,
            DashboardAction.OpenEnvironment => DashboardView.Environment,
            _ => null,
        };

        private void SwitchView(DashboardView view)
        {
            lock (_gate)
            {
                _currentView = view;
                _selectedRow = 0;
                _helpOpen = false;
                _dirty = true;
            }
        }

        private void MoveSelection(int delta)
        {
            lock (_gate)
            {
                if (_snapshot is null) return;
                var count = CountRowsIn(_currentView, _snapshot);
                if (count <= 0) return;
                if (_currentView == DashboardView.Home)
                {
                    _homeMenuIndex = ((_homeMenuIndex + delta) % count + count) % count;
                }
                else
                {
                    _selectedRow = ((_selectedRow + delta) % count + count) % count;
                }
                _dirty = true;
            }
        }

        /// <summary>
        /// How many selectable rows live in the given view? Home has 5 (one per menu item);
        /// detail views report their data-row count (or 1 for Embeddings / 0 for Environment,
        /// which is read-only).
        /// </summary>
        private static int CountRowsIn(DashboardView view, DashboardSnapshot snapshot) => view switch
        {
            DashboardView.Home => DashboardRenderer.HomeMenuEntries.Length,
            DashboardView.Scopes => snapshot.Scopes.Count,
            DashboardView.Clients => snapshot.Clients.Count,
            DashboardView.Embeddings => 1,
            DashboardView.RecentActivity => Math.Min(snapshot.RecentActivity.Count, 24),
            DashboardView.Environment => 0,
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
            DashboardView view;
            int homeMenuIndex;
            int selectedRow;
            bool helpOpen;
            ToastState toast;
            lock (_gate)
            {
                snap = _snapshot;
                view = _currentView;
                homeMenuIndex = _homeMenuIndex;
                selectedRow = _selectedRow;
                helpOpen = _helpOpen;
                toast = _toast;
            }

            if (snap is null)
            {
                layout[DashboardLayout.HeaderRegion].Update(new Panel(new Markup("[grey]Loading snapshot…[/]")) { Border = BoxBorder.None });
                return;
            }

            layout[DashboardLayout.HeaderRegion].Update(DashboardRenderer.BuildHeader(snap, _renderOptions, view));

            IRenderable body = view switch
            {
                DashboardView.Home => DashboardRenderer.BuildHome(snap, _renderOptions, homeMenuIndex),
                DashboardView.Scopes => DashboardRenderer.BuildScopesDetail(snap, _renderOptions, selectedRow),
                DashboardView.Clients => DashboardRenderer.BuildClientsDetail(snap, _renderOptions, selectedRow),
                DashboardView.Embeddings => DashboardRenderer.BuildEmbeddingsDetail(snap, _renderOptions),
                DashboardView.RecentActivity => DashboardRenderer.BuildRecentActivityDetail(snap, _renderOptions, selectedRow),
                DashboardView.Environment => DashboardRenderer.BuildEnvironmentDetail(snap, _renderOptions),
                _ => DashboardRenderer.BuildHome(snap, _renderOptions, homeMenuIndex),
            };
            layout[DashboardLayout.BodyRegion].Update(body);

            var footer = helpOpen
                ? RenderHelpFooter()
                : DashboardRenderer.BuildFooter(toast, view);
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
/// Gates which dashboard actions are allowed in which views. Lifted out of
/// <see cref="DashboardCli"/>'s private LoopState so unit tests can pin the table without
/// standing up the full loop.
///
/// <para>
/// The rule: section-specific action keys (<c>r</c>, <c>R</c>, <c>w</c>, <c>u</c>, <c>p</c>,
/// <c>v</c>) only fire their action in the matching detail view; firing them from another view
/// is dropped silently. View-agnostic actions (quit, force refresh, guided actions, view
/// transitions) are always allowed.
/// </para>
/// </summary>
internal static class DashboardViewGating
{
    /// <summary>
    /// True iff <paramref name="action"/> is allowed to dispatch in <paramref name="view"/>.
    /// Returns false for section-specific actions targeted at the wrong view, true otherwise.
    /// </summary>
    public static bool IsAllowed(DashboardAction action, DashboardView view) => action switch
    {
        // View-agnostic actions — always allowed.
        DashboardAction.Quit or DashboardAction.ForceRefresh or
        DashboardAction.ToggleHelp or DashboardAction.None => true,
        DashboardAction.InitGuided or DashboardAction.DemoGuided or
        DashboardAction.OpenLogInPager or DashboardAction.OpenConfigInEditor => true,
        // View transitions — always allowed (the dispatcher handles them inline).
        DashboardAction.GoHome or DashboardAction.OpenScopes or
        DashboardAction.OpenClients or DashboardAction.OpenEmbeddings or
        DashboardAction.OpenRecentActivity or DashboardAction.OpenEnvironment => true,

        // Scopes-only
        DashboardAction.ReindexScope or DashboardAction.RebuildScope => view == DashboardView.Scopes,
        // Clients-only
        DashboardAction.WireClient or DashboardAction.UnwireClient => view == DashboardView.Clients,
        // Embeddings-only
        DashboardAction.EmbeddingsPull or DashboardAction.EmbeddingsVerify => view == DashboardView.Embeddings,

        _ => true,
    };
}

/// <summary>
/// Routes the <c>Enter</c> (PrimaryAction) key inside a detail view to a concrete action based
/// on which view is active. Lifted out of <see cref="DashboardCli"/>'s private LoopState so unit
/// tests can pin the per-view dispatch (including the Clients wire/unwire toggle) without
/// standing up the full loop.
/// </summary>
internal static class DashboardPrimaryAction
{
    /// <summary>
    /// Map <c>Enter</c> (PrimaryAction) to the section-specific concrete action for a detail
    /// view. Home is handled directly by the loop (open the highlighted menu item) and never
    /// reaches this method.
    /// </summary>
    public static DashboardAction ResolveForView(DashboardView view, int selectedRow, DashboardSnapshot snap) =>
        view switch
        {
            DashboardView.Scopes => DashboardAction.ReindexScope,
            DashboardView.Clients => ResolveClientToggle(selectedRow, snap),
            DashboardView.Embeddings => DashboardAction.EmbeddingsPull,
            DashboardView.RecentActivity => DashboardAction.None,
            DashboardView.Environment => DashboardAction.None,
            _ => DashboardAction.None,
        };

    /// <summary>
    /// Legacy entry point preserved for unit tests that drove the previous section-based
    /// resolver. Re-implemented in terms of <see cref="ResolveForView"/> so the two paths can't
    /// drift.
    /// </summary>
    public static DashboardAction Resolve(DashboardSelection sel, DashboardSnapshot snap)
    {
        var view = sel.FocusedSection switch
        {
            DashboardSection.Scopes => DashboardView.Scopes,
            DashboardSection.Clients => DashboardView.Clients,
            DashboardSection.Embeddings => DashboardView.Embeddings,
            DashboardSection.RecentActivity => DashboardView.RecentActivity,
            DashboardSection.Environment => DashboardView.Environment,
            _ => DashboardView.Home,
        };
        return ResolveForView(view, sel.RowIndex, snap);
    }

    private static DashboardAction ResolveClientToggle(int selectedRow, DashboardSnapshot snap)
    {
        // Mirror DashboardActions.OrderedClients ordering so the same row index resolves.
        var clients = snap.Clients.OrderBy(c => c.Scope == "project" ? 0 : 1).ToArray();
        if (selectedRow < 0 || selectedRow >= clients.Length) return DashboardAction.WireClient;
        return clients[selectedRow].ContainsSourcegraphEntry
            ? DashboardAction.UnwireClient
            : DashboardAction.WireClient;
    }
}

using DevBitsLab.Mcp.SourceGraph.Server.Dashboard;
using FluentAssertions;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests.Dashboard;

/// <summary>
/// Covers <see cref="DashboardKeyMap"/>: every documented binding resolves to exactly one action,
/// alias bindings (<c>j</c> ≡ <c>↓</c>, <c>k</c> ≡ <c>↑</c>, <c>h</c> ≡ <c>Esc</c>) resolve to
/// the same target, and unmapped keys return <c>false</c> without throwing.
/// </summary>
public sealed class DashboardKeyMapTests
{
    // Pass DashboardAction as int in the [MemberData] tuples — the type is internal and xunit
    // 2.x's row serialiser only handles types public-visible from outside the assembly. The
    // method casts back inside the body.
    public static IEnumerable<object[]> DocumentedKeys()
    {
        // (description, ConsoleKey, KeyChar, modifiers, expected action as int)
        yield return new object[] { "UpArrow", ConsoleKey.UpArrow, '\0', (int)ConsoleModifiers.None, (int)DashboardAction.MoveUp };
        yield return new object[] { "DownArrow", ConsoleKey.DownArrow, '\0', (int)ConsoleModifiers.None, (int)DashboardAction.MoveDown };
        yield return new object[] { "Enter", ConsoleKey.Enter, '\r', (int)ConsoleModifiers.None, (int)DashboardAction.PrimaryAction };
        // Esc and 'h' both fire GoHome under the new view-aware model.
        yield return new object[] { "Esc", ConsoleKey.Escape, (char)27, (int)ConsoleModifiers.None, (int)DashboardAction.GoHome };
        yield return new object[] { "h home", ConsoleKey.H, 'h', (int)ConsoleModifiers.None, (int)DashboardAction.GoHome };
        yield return new object[] { "q", ConsoleKey.Q, 'q', (int)ConsoleModifiers.None, (int)DashboardAction.Quit };
        yield return new object[] { "Q", ConsoleKey.Q, 'Q', (int)ConsoleModifiers.Shift, (int)DashboardAction.Quit };
        yield return new object[] { "Ctrl+C", ConsoleKey.C, '\u0003', (int)ConsoleModifiers.Control, (int)DashboardAction.Quit };
        yield return new object[] { "?", ConsoleKey.Oem2, '?', (int)ConsoleModifiers.Shift, (int)DashboardAction.ToggleHelp };
        yield return new object[] { "s", ConsoleKey.S, 's', (int)ConsoleModifiers.None, (int)DashboardAction.ForceRefresh };
        yield return new object[] { "j alias for down", ConsoleKey.J, 'j', (int)ConsoleModifiers.None, (int)DashboardAction.MoveDown };
        yield return new object[] { "k alias for up", ConsoleKey.K, 'k', (int)ConsoleModifiers.None, (int)DashboardAction.MoveUp };
        // Numeric jumps from home (also work from detail views — dispatcher resolves the transition).
        yield return new object[] { "1 → Scopes", ConsoleKey.D1, '1', (int)ConsoleModifiers.None, (int)DashboardAction.OpenScopes };
        yield return new object[] { "2 → Clients", ConsoleKey.D2, '2', (int)ConsoleModifiers.None, (int)DashboardAction.OpenClients };
        yield return new object[] { "3 → Embeddings", ConsoleKey.D3, '3', (int)ConsoleModifiers.None, (int)DashboardAction.OpenEmbeddings };
        yield return new object[] { "4 → Recent activity", ConsoleKey.D4, '4', (int)ConsoleModifiers.None, (int)DashboardAction.OpenRecentActivity };
        yield return new object[] { "5 → Environment", ConsoleKey.D5, '5', (int)ConsoleModifiers.None, (int)DashboardAction.OpenEnvironment };
        // Section actions — keymap is view-agnostic; dispatcher gates per-view.
        yield return new object[] { "r reindex", ConsoleKey.R, 'r', (int)ConsoleModifiers.None, (int)DashboardAction.ReindexScope };
        yield return new object[] { "R rebuild", ConsoleKey.R, 'R', (int)ConsoleModifiers.Shift, (int)DashboardAction.RebuildScope };
        yield return new object[] { "N new scope", ConsoleKey.N, 'N', (int)ConsoleModifiers.Shift, (int)DashboardAction.AddScope };
        yield return new object[] { "D delete scope", ConsoleKey.D, 'D', (int)ConsoleModifiers.Shift, (int)DashboardAction.RemoveScope };
        yield return new object[] { "w wire", ConsoleKey.W, 'w', (int)ConsoleModifiers.None, (int)DashboardAction.WireClient };
        yield return new object[] { "u unwire", ConsoleKey.U, 'u', (int)ConsoleModifiers.None, (int)DashboardAction.UnwireClient };
        yield return new object[] { "p pull", ConsoleKey.P, 'p', (int)ConsoleModifiers.None, (int)DashboardAction.EmbeddingsPull };
        yield return new object[] { "v verify", ConsoleKey.V, 'v', (int)ConsoleModifiers.None, (int)DashboardAction.EmbeddingsVerify };
        yield return new object[] { "i init", ConsoleKey.I, 'i', (int)ConsoleModifiers.None, (int)DashboardAction.InitGuided };
        yield return new object[] { "d demo", ConsoleKey.D, 'd', (int)ConsoleModifiers.None, (int)DashboardAction.DemoGuided };
        yield return new object[] { "l log pager", ConsoleKey.L, 'l', (int)ConsoleModifiers.None, (int)DashboardAction.OpenLogInPager };
        yield return new object[] { "e editor", ConsoleKey.E, 'e', (int)ConsoleModifiers.None, (int)DashboardAction.OpenConfigInEditor };
    }

    [Theory]
    [MemberData(nameof(DocumentedKeys))]
    public void TryResolve_documentedKey_resolvesToExpectedAction(string description, ConsoleKey key, char keyChar, int modifiers, int expectedInt)
    {
        var expected = (DashboardAction)expectedInt;
        var info = new ConsoleKeyInfo(keyChar, key,
            shift: ((ConsoleModifiers)modifiers & ConsoleModifiers.Shift) != 0,
            alt: ((ConsoleModifiers)modifiers & ConsoleModifiers.Alt) != 0,
            control: ((ConsoleModifiers)modifiers & ConsoleModifiers.Control) != 0);
        var ok = DashboardKeyMap.TryResolve(info, out var action);
        ok.Should().BeTrue($"binding '{description}' should resolve");
        action.Should().Be(expected, $"binding '{description}'");
    }

    [Fact]
    public void TryResolve_Tab_isNoLongerBound()
    {
        // Tab cycle was removed in the home/detail-view rewrite; Tab now resolves to None so
        // pressing it from any view is silently dropped (no spurious navigation).
        var info = new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift: false, alt: false, control: false);
        DashboardKeyMap.TryResolve(info, out var action).Should().BeFalse();
        action.Should().Be(DashboardAction.None);
    }

    [Fact]
    public void TryResolve_unmappedLetter_returnsFalse()
    {
        // 'z' is unmapped; should return false without throwing.
        var info = new ConsoleKeyInfo('z', ConsoleKey.Z, shift: false, alt: false, control: false);
        var ok = DashboardKeyMap.TryResolve(info, out var action);
        ok.Should().BeFalse();
        action.Should().Be(DashboardAction.None);
    }

    [Fact]
    public void TryResolve_F1_returnsFalse()
    {
        // Function keys are unmapped.
        var info = new ConsoleKeyInfo('\0', ConsoleKey.F1, shift: false, alt: false, control: false);
        DashboardKeyMap.TryResolve(info, out _).Should().BeFalse();
    }

    [Fact]
    public void TryResolve_jAndDownArrow_resolveToSameAction()
    {
        // Alias invariant: j is the documented vim-style alias for ↓.
        DashboardKeyMap.TryResolve(new ConsoleKeyInfo('j', ConsoleKey.J, false, false, false), out var ja);
        DashboardKeyMap.TryResolve(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false), out var down);
        ja.Should().Be(down);
    }

    [Fact]
    public void TryResolve_kAndUpArrow_resolveToSameAction()
    {
        DashboardKeyMap.TryResolve(new ConsoleKeyInfo('k', ConsoleKey.K, false, false, false), out var ka);
        DashboardKeyMap.TryResolve(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false), out var up);
        ka.Should().Be(up);
    }

    [Fact]
    public void TryResolve_hAndEsc_resolveToSameAction()
    {
        // 'h' is the documented vim-style alias for Esc → GoHome.
        DashboardKeyMap.TryResolve(new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false), out var ha);
        DashboardKeyMap.TryResolve(new ConsoleKeyInfo((char)27, ConsoleKey.Escape, false, false, false), out var esc);
        ha.Should().Be(esc);
        ha.Should().Be(DashboardAction.GoHome);
    }

    [Fact]
    public void For_home_advertisesMenuKeys()
    {
        // The home-view footer hint mentions selection + open + jump + help + quit.
        var hint = DashboardKeyMap.For(DashboardView.Home);
        hint.Should().Contain("select");
        hint.Should().Contain("open");
        hint.Should().Contain("quit");
    }

    [Fact]
    public void For_scopes_advertisesScopeActionKeys()
    {
        var hint = DashboardKeyMap.For(DashboardView.Scopes);
        hint.Should().Contain("reindex");
        hint.Should().Contain("rebuild");
        hint.Should().Contain("new");      // [N] new scope
        hint.Should().Contain("delete");   // [D] delete scope
        hint.Should().Contain("home");
    }

    [Fact]
    public void For_clients_advertisesWireUnwire()
    {
        var hint = DashboardKeyMap.For(DashboardView.Clients);
        hint.Should().Contain("wire");
        hint.Should().Contain("unwire");
    }

    [Fact]
    public void For_environment_isHomeAndQuitOnly()
    {
        var hint = DashboardKeyMap.For(DashboardView.Environment);
        hint.Should().Contain("home");
        hint.Should().Contain("quit");
        hint.Should().NotContain("reindex");
        hint.Should().NotContain("wire");
        hint.Should().NotContain("pull");
    }

    [Fact]
    public void HelpText_mentionsEveryDocumentedKey()
    {
        // Sanity: the on-screen help text should at least mention each tier of key. Catches the
        // common drift where the table grows but the inline overlay forgets a binding.
        DashboardKeyMap.HelpText.Should()
            .Contain("Navigation")
            .And.Contain("Reindex")
            .And.Contain("Rebuild")
            .And.Contain("New scope")
            .And.Contain("Delete selected scope")
            .And.Contain("Wire")
            .And.Contain("Unwire")
            .And.Contain("Embeddings pull")
            .And.Contain("Embeddings verify")
            .And.Contain("init")
            .And.Contain("demo")
            // Home navigation should be discoverable in the help.
            .And.Contain("Home");
    }
}

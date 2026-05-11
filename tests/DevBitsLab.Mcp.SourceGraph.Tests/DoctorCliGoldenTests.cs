using System.Text.RegularExpressions;
using DevBitsLab.Mcp.SourceGraph.Server.Cli;
using Xunit;

namespace DevBitsLab.Mcp.SourceGraph.Tests;

/// <summary>
/// Golden-file parity tests for the <c>doctor</c> subcommand. The intent is to pin the
/// observable output bytes — both prose and <c>--json</c> — through the upcoming refactor
/// of <c>DoctorCli</c> onto the snapshot. If a future change shifts the wording, the
/// emitted shape, or the exit-code semantics, these tests will fail at the diff site and
/// the change author can update the golden file deliberately.
///
/// <para>
/// Machine-specific values (SDK version, home-relative paths, cache sizes) are normalised
/// to placeholders before comparison so the goldens are stable across dev machines and CI
/// runners. Normalisation is intentionally narrow: only fields the doctor surface emits
/// from <see cref="OnboardingDetector"/>'s detection or from <c>ModelStore.DefaultCacheDir</c>.
/// </para>
/// <para>
/// To keep the goldens deterministic across hosts — a dev machine with an embedding cache
/// and Claude Desktop installed produces different doctor output than a fresh CI runner —
/// the fixture isolates <c>HOME</c> / <c>USERPROFILE</c> / <c>XDG_CACHE_HOME</c> /
/// <c>LOCALAPPDATA</c> / <c>APPDATA</c> to temp directories for the duration of each test.
/// Detection then sees a clean home with no cache and no client configs, which matches the
/// shape committed in the golden files.
/// </para>
/// </summary>
[Collection("CliConsole")]
public sealed class DoctorCliGoldenTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _isolatedHome;
    private readonly string _isolatedCache;
    private readonly TextWriter _originalStdout;
    private readonly TextWriter _originalStderr;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly Dictionary<string, string?> _savedEnv = new();

    public DoctorCliGoldenTests()
    {
        _tempRoot = Path.Join(Path.GetTempPath(), "sg-doctor-golden-" + Guid.NewGuid().ToString("N"));
        _isolatedHome = Path.Join(Path.GetTempPath(), "sg-doctor-home-" + Guid.NewGuid().ToString("N"));
        _isolatedCache = Path.Join(Path.GetTempPath(), "sg-doctor-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_isolatedHome);
        Directory.CreateDirectory(_isolatedCache);

        // Isolate detection-relevant env vars so the golden output is independent of the dev
        // machine's actual state (an existing embedding cache or a Claude Desktop install would
        // otherwise leak into the doctor output and diverge from CI).
        SaveAndSet("HOME", _isolatedHome);
        SaveAndSet("USERPROFILE", _isolatedHome);
        SaveAndSet("XDG_CACHE_HOME", _isolatedCache);
        SaveAndSet("LOCALAPPDATA", _isolatedCache);
        SaveAndSet("APPDATA", Path.Join(_isolatedHome, "AppData", "Roaming"));

        _originalStdout = Console.Out;
        _originalStderr = Console.Error;
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
    }

    public void Dispose()
    {
        Console.SetOut(_originalStdout);
        Console.SetError(_originalStderr);
        foreach (var (key, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        try { Directory.Delete(_isolatedHome, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        try { Directory.Delete(_isolatedCache, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private void SaveAndSet(string name, string value)
    {
        _savedEnv[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    [Fact]
    public async Task Doctor_json_healthy_matchesGolden()
    {
        File.WriteAllText(Path.Join(_tempRoot, "Test.slnx"), "<Solution/>");
        var cli = CommandLine.Parse(new[] { "doctor", "--root", _tempRoot, "--json" });
        var rc = await DoctorCli.RunAsync(cli);
        Assert.Contains(rc, new[] { 0, 2 });
        var actual = Normalise(_stdout.ToString(), _tempRoot);
        AssertOrCreateGolden(actual, "healthy.json");
    }

    [Fact]
    public async Task Doctor_json_partial_matchesGolden()
    {
        // "Partial" fixture: a malformed `.sourcegraph.json` triggers a hard-fail check;
        // the other checks pass. Pinning the json output catches any change in either the
        // human-readable message text or the json field ordering.
        File.WriteAllText(Path.Join(_tempRoot, ".sourcegraph.json"), "{ this is broken");
        File.WriteAllText(Path.Join(_tempRoot, "Test.slnx"), "<Solution/>");
        var cli = CommandLine.Parse(new[] { "doctor", "--root", _tempRoot, "--json" });
        var rc = await DoctorCli.RunAsync(cli);
        Assert.Equal(1, rc);
        var actual = Normalise(_stdout.ToString(), _tempRoot);
        AssertOrCreateGolden(actual, "partial.json");
    }

    [Fact]
    public async Task Doctor_human_healthy_matchesGolden()
    {
        File.WriteAllText(Path.Join(_tempRoot, "Test.slnx"), "<Solution/>");
        var cli = CommandLine.Parse(new[] { "doctor", "--root", _tempRoot });
        var rc = await DoctorCli.RunAsync(cli);
        Assert.Contains(rc, new[] { 0, 2 });
        var actual = Normalise(_stdout.ToString(), _tempRoot);
        AssertOrCreateGolden(actual, "healthy.human.txt");
    }

    /// <summary>
    /// Compare <paramref name="actual"/> against the bytes at <c>tests/.../DoctorCli/golden/{name}</c>.
    /// If the golden file doesn't yet exist, write it and fail loudly so the change author knows
    /// they're seeding a new baseline rather than asserting against one.
    /// </summary>
    /// <remarks>
    /// We deliberately avoid FluentAssertions' <c>.Should().Be(expected, because)</c> here: its
    /// failure-message formatter routes the failure text (which embeds <paramref name="actual"/>)
    /// through <see cref="string.Format(string,object[])"/>, and JSON output's <c>{</c>/<c>}</c>
    /// literals get interpreted as format placeholders, which throws before the real diff is
    /// shown. xunit's <see cref="Assert.Equal(string,string)"/> formats no template — it just
    /// reports the byte-diff position cleanly.
    /// </remarks>
    private static void AssertOrCreateGolden(string actual, string name)
    {
        var goldenPath = LocateGoldenFile(name);
        if (!File.Exists(goldenPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            File.WriteAllText(goldenPath, actual);
            throw new Xunit.Sdk.XunitException(
                $"Golden file '{name}' did not exist; wrote a fresh baseline at {goldenPath}. " +
                "Re-run the test to assert against the newly-seeded golden.");
        }
        var expected = File.ReadAllText(goldenPath);
        Assert.Equal(expected, actual);
    }

    private static string LocateGoldenFile(string name)
    {
        // Walk upward from the test assembly's location until we find a directory that contains
        // tests/DevBitsLab.Mcp.SourceGraph.Tests. That's the repo's tests dir.
        var current = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Join(current, "tests", "DevBitsLab.Mcp.SourceGraph.Tests", "DoctorCli", "golden", name);
            if (Directory.Exists(Path.GetDirectoryName(candidate)!))
            {
                return candidate;
            }
            // Try repo-root-anchored layout for the case where current is inside bin/.
            var local = Path.Join(current, "DoctorCli", "golden", name);
            if (Directory.Exists(Path.GetDirectoryName(local)!))
            {
                return local;
            }
            current = Path.GetDirectoryName(current) ?? current;
        }
        // Final fallback: drop the file next to the test assembly (CI sandboxes don't have a
        // writeable source tree, so the AssertOrCreateGolden path will write a fresh baseline
        // there if needed and the assertion will run against that copy on subsequent runs).
        return Path.Join(AppContext.BaseDirectory, "DoctorCli", "golden", name);
    }

    /// <summary>
    /// Strip machine-specific bits from a doctor output so the golden file is stable across
    /// dev machines and CI runners. Replaces:
    /// - <c>.NET SDK 9.0.123</c> → <c>.NET SDK __SDK_VERSION__</c>
    /// - Absolute temp-root paths → <c>__ROOT__</c>
    /// - User home directory → <c>__HOME__</c>
    /// - Cache size <c>(NNN MB)</c> → <c>(__SIZE__)</c>
    /// - Fluent Assertions' license preamble (it's written to <see cref="Console.Out"/> on
    ///   first use and leaks into our capture; pre-stripped so the golden stays focused on
    ///   the actual doctor output).
    /// - Windows backslash separators collapsed to forward slashes so the same goldens work
    ///   on Linux / macOS / Windows runners (path separators aren't part of doctor's
    ///   observable contract).
    /// </summary>
    private static string Normalise(string s, string tempRoot)
    {
        // Fluent Assertions writes a license preamble starting with "     Warning:" the first
        // time an assertion runs in the process. It's a Console.Out side effect, not part of
        // doctor's output; strip everything from that marker to the end before normalising the
        // doctor surface.
        var faIdx = s.IndexOf("\n     Warning:", StringComparison.Ordinal);
        if (faIdx >= 0)
        {
            s = s.Substring(0, faIdx);
            // Drop any trailing whitespace/newlines left after truncation.
            s = s.TrimEnd() + "\n";
        }

        // Path-separator normalisation. The two output forms (JSON vs human-readable) need
        // different handling and are mutually exclusive in a single call, so we branch on the
        // first non-whitespace character:
        //
        // - JSON: a lone `\` is part of an escape sequence (`—`, `'`, `\n`, …) and
        //   MUST NOT be touched. A Windows path separator appears as the escaped form `\\`
        //   (two chars). We collapse only `\\` → `/`.
        //
        // - Human-readable: backslashes only appear as Windows path separators (the doctor
        //   surface emits no other backslash use), so a blanket single `\` → `/` is safe.
        //
        // After the in-string normalisation, the local `tempRoot` / `home` variables we replace
        // below are also normalised to `/` so they match the post-normalisation form of `s`.
        var isJson = s.TrimStart().StartsWith('{');
        s = isJson ? s.Replace(@"\\", "/") : s.Replace('\\', '/');
        tempRoot = tempRoot.Replace('\\', '/');

        s = s.Replace(tempRoot, "__ROOT__");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolderOption.DoNotVerify);
        if (!string.IsNullOrEmpty(home))
        {
            home = home.Replace('\\', '/');
            s = s.Replace(home, "__HOME__");
        }
        // Normalise common machine-specific fields.
        s = Regex.Replace(s, @"\.NET SDK \d+\.\d+\.\d+", ".NET SDK __SDK_VERSION__");
        s = Regex.Replace(s, @"\(\d+ MB\)", "(__SIZE__)");
        // Default cache may live under either XDG_CACHE_HOME (linux), %LOCALAPPDATA% (win),
        // or ~/.cache (mac). The home substitution above usually catches it, but XDG variants
        // can override the path entirely; collapse the absolute prefix here. Also collapse the
        // home-prefix-then-cache combination so the post-home replacement reads naturally.
        s = Regex.Replace(s, @"__HOME__/\.cache/devbitslab\.sourcegraph/models", "__CACHE_DIR__");
        s = Regex.Replace(s, @"/[A-Za-z0-9_./-]+/devbitslab\.sourcegraph/models", "__CACHE_DIR__");
        return s;
    }
}

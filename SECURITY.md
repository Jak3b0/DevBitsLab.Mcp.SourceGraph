# Security Policy

Thank you for taking the time to improve the security of
`DevBitsLab.Mcp.SourceGraph`.

## Supported versions

`DevBitsLab.Mcp.SourceGraph` is pre-1.0. Security fixes are issued on the
**latest minor** of the `0.x` line published to NuGet. Older `0.x` minors are
not patched — please upgrade to the most recent release.

| Package                                | Version      | Supported |
|----------------------------------------|--------------|:---------:|
| `DevBitsLab.Mcp.SourceGraph.Tool`      | latest 0.x   | Yes       |
| `DevBitsLab.Mcp.SourceGraph.Tool`      | older 0.x    | No        |
| `DevBitsLab.Mcp.SourceGraph.Sdk`       | latest 1.x   | Yes       |
| `DevBitsLab.Mcp.SourceGraph.Sdk`       | older 1.x    | No        |

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues,
discussions, or pull requests.**

Use one of the following private channels instead:

1. **GitHub Security Advisory** *(preferred)* — open a private report at
   <https://github.com/Jak3b0/DevBitsLab.Mcp.SourceGraph/security/advisories/new>.
2. **Email** — `jacques.bourque@gmail.com` with subject
   `[security] DevBitsLab.Mcp.SourceGraph: <short description>`.

A useful report includes:

- A short description of the issue and the impact.
- The package and version affected (`Tool` or `Sdk`, e.g. `0.7.0`).
- Steps to reproduce — ideally a minimal solution / repo or a unit test.
- Any logs, stack traces, or `.sourcegraph/usage.jsonl` excerpts that help.
- Whether you have a proposed fix.

We aim to acknowledge new reports within **3 business days** and to ship a fix
or a documented mitigation within **30 days** for high-severity issues.

## Scope

The following are **in scope**:

- Path traversal via solution / database / scope paths.
- Arbitrary code execution via crafted MSBuild project files, Roslyn analyzer
  payloads, or plugin assemblies loaded from untrusted locations.
- SQL injection or FTS5 query injection through MCP tool arguments.
- Denial of service through unbounded queries, runaway indexing, or pathological
  watcher events.
- Sensitive data disclosure via `.sourcegraph/` artifacts (log files, embeddings,
  cached blame).
- Supply-chain weaknesses in our published NuGet packages (missing source-link,
  missing repository metadata, deterministic-build regressions).

The following are **out of scope** (please report upstream):

- Vulnerabilities in the .NET runtime or BCL (report to <https://msrc.microsoft.com>).
- Vulnerabilities in Roslyn / MSBuild / `Microsoft.CodeAnalysis.*` (report to <https://msrc.microsoft.com>).
- Vulnerabilities in `ModelContextProtocol`, `Microsoft.Data.Sqlite`,
  `Dapper`, `sqlite-vec`, `Microsoft.ML.OnnxRuntime`, or other transitive
  dependencies — please report to those projects directly. We track Dependabot
  alerts and ship updates as fast as we can.

## Disclosure

We follow **coordinated disclosure**. Once a fix is available we will:

1. Publish a patched release to NuGet.
2. File a GitHub Security Advisory crediting the reporter (unless you ask to
   remain anonymous).
3. Add an entry to `CHANGELOG.md`.

## Hardening guidance for operators

Operators running this server inside an enterprise environment should
additionally:

- Pin a specific tool version per repo via `dotnet tool install
  DevBitsLab.Mcp.SourceGraph.Tool` against a tool manifest, rather than the
  global install.
- Prefer the `--no-instructions` and `--no-history` flags in CI to avoid
  emitting model-facing guidance and to skip `git blame` shell-outs.
- Run the server as a non-privileged user. The server only needs read access to
  the source tree and read/write access to `<root>/.sourcegraph/`.
- Restrict plugins (`plugins[]` in `.sourcegraph.json`) to assemblies you have
  reviewed. Plugins run inside the host process.
- Inspect `.sourcegraph/usage.jsonl` if you need an auditable record of every
  tool call answered by the graph.

Strong-naming and Authenticode signing of published packages is on the roadmap
but not yet shipped — see [GOVERNANCE.md](GOVERNANCE.md) for status.

## Data retention and sensitive information

The server stores operational data in `<root>/.sourcegraph/`:

- **Usage logs** (`usage.jsonl`, `heals.jsonl`) — tool call history with
  parameters, timestamps, and durations. These logs may contain sensitive query
  data (e.g., SQL statements with embedded identifiers from your codebase).
- **Embeddings cache** (`scopes/<id>.db` embeddings table) — semantic vectors
  derived from your source code.
- **Blame cache** (`scopes/<id>.db` history table) — author names and commit
  timestamps from `git blame`.
- **Symbol index** (`scopes/<id>.db`) — symbol names, types, file paths from
  your codebase.

**Recommended practices:**

1. **Add `.sourcegraph/` to your `.gitignore`** — these artifacts are local
   workspace caches and should not be committed to version control. The `init`
   command reminds you to do this.
2. **Retention policy** — usage logs rotate automatically when they exceed 10 MB
   (oldest entries removed). Embeddings and indexes remain until `clear` or
   `repair` commands are invoked. If you need stricter retention (e.g., GDPR
   compliance for author names in blame cache), delete the `.sourcegraph/`
   directory periodically or use `--no-history` to skip blame indexing entirely.
3. **Redaction** — if you share logs or databases for debugging, sanitize them
   first. Usage logs are JSONL and can be filtered with `jq`. Consider scrubbing
   `details.sql` fields if they contain proprietary identifiers.

## Plugin trust model

**Plugins execute with full host privileges** — they run in-process using a
shared `AssemblyLoadContext` and have unrestricted access to the host's
capabilities (file I/O, network, process spawning). Only load plugins from
trusted sources.

**NuGet plugin restore:**

- The server runs `dotnet restore` to fetch plugins declared in
  `.sourcegraph.json`. NuGet package signature verification is **not enforced**
  by the server at this time — rely on your organization's NuGet feed
  restrictions and package signing policies.
- If a plugin's NuGet source is compromised, or if you specify an untrusted
  package ID, malicious code can execute during indexing or tool invocation.

**Recommendations:**

- Audit every plugin assembly before adding it to `plugins[]`.
- Use a private NuGet feed with signature enforcement for enterprise
  deployments.
- Pin plugin versions explicitly (e.g., `"version": "1.2.3"` in
  `.sourcegraph.json`) to avoid unexpected updates.
- Future versions may add opt-in cryptographic signature verification via a
  `--verify-plugins` flag (tracked in roadmap).

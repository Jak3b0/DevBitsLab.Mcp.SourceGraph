# Manual smoke test — operator dashboard

Live-TUI interactions are hard to drive deterministically from xunit (Spectre's `Live` mode
takes ownership of the console; `Console.ReadKey` blocks on the real terminal). The unit suite
covers everything that's testable headlessly — key map, renderer, freshness coalescing,
bare-command dispatch, action gating, watchdog — but the keypress-to-pixel pipeline still needs
a human pass before merge.

Run each checklist item in order, against a clean `dotnet build` of the worktree.

## Set-up

1. `dotnet build` — confirm green build.
2. Open a terminal at least 100×40 cells. The dashboard refuses anything below 80×24 and the
   golden Spectre layout looks cramped under that threshold.
3. `cd <repo-root>` so the dashboard reads this repo's `.sourcegraph/` artefacts.

## Launch

4. **Bare invocation under a tty.** Run `sourcegraph-mcp` with no args. Expected: the dashboard
   launches (header `🌿 SourceGraph v… ~/…/repo`, five sections, footer `[q] quit  [?] help …`).
5. **Bare invocation under a pipe.** Run `sourcegraph-mcp | cat`. Expected: a single
   `status`-shape snapshot prints to stdout; no Spectre rendering; exit code reflects health.
6. **Explicit dashboard.** Run `sourcegraph-mcp dashboard`. Same as #4.
7. **Tiny terminal refuses.** Resize the terminal to ~60×20 and run `sourcegraph-mcp dashboard`.
   Expected: stderr `terminal too small (need ≥80×24)`, exit code 2, no Spectre rendering.

## Navigation

8. **Arrow keys.** Press ↓ several times; confirm the cursor ▶ moves within the focused section.
9. **j / k aliases.** Press `j` and `k`; confirm equivalent movement.
10. **Number keys 1–5.** From the home view, confirm `1` / `2` / `3` / `4` / `5` jump to Scopes / Clients / Embeddings / Recent activity / Environment. `Esc` or `h` returns to home. (Tab / Shift+Tab were unbound by the later home/detail-view rewrite; the original proposal listed them here.)
11. **? help overlay.** Press `?`; confirm a footer overlay listing every documented key. Press
    `?` again or `Esc` to close.
12. **s force refresh.** Press `s`; status bar should briefly show `snapshot refresh requested`.

## Live updates

13. In a second shell, append a line to `.sourcegraph/usage.jsonl`. Expected: the `Recent
    activity` section shows the new row within ~200 ms.
14. Run `sourcegraph-mcp serve --solution ...` in the other shell. Expected: any scope status
    transitions surface in the Scopes section within ~1 second.

## Read-only quit / Ctrl+C

15. Press `q`. Expected: clean exit code 0, terminal cursor restored, no leftover ANSI
    sequences.
16. Re-launch, press `Ctrl+C`. Expected: same as #15.

## Confirm-modal gating

17. With a wired client (e.g. `claude-code`) selected in the Clients section, press `u`.
    Expected: `unwire claude-code? [y/N]`. Press `Esc` → no file changes; status shows
    `unwire cancelled`.
18. Repeat #17 and answer `y`. Expected: the `.mcp.json` loses its `sourcegraph` entry; the
    Clients section's row glyph flips from 🌿 to · within a second.
19. With a scope selected in the Scopes section, press `R`. Expected: `rebuild <name>? [y/N]`.
    Press `n`; status shows `rebuild cancelled`. (Do NOT smoke `y` on a large repo — that
    archives the DB and re-indexes from sources; manual testers know the cost.)

## In-place no-gate

20. Press `r` on a scope row. Expected: status shows `reindexed scope '<name>'` or an error if
    no solution is resolved.
21. Press `p` on the Embeddings section (or anywhere — pull is global). Expected: status shows
    `pulled <model>` after a beat (or `pull failed: ...` on no network).
22. Press `v`. Expected: status shows `verified <model>` or `verify mismatch on N file(s)`.

## Guided actions (suspend + subprocess)

23. Press `i`. Expected: Spectre Live region clears; `sourcegraph-mcp init` runs visibly (full
    interactive picker). On exit, the dashboard redraws.
24. Press `d` on a scope row. Expected: same shape — `sourcegraph-mcp demo --scope <name>`
    runs to completion; dashboard redraws.
25. Press `l`. Expected: `less -R .sourcegraph/usage.jsonl` (or `$PAGER`); on exit, dashboard
    redraws.
26. Press `e`. Expected: `vi .sourcegraph.json` (or `$EDITOR`); on exit, dashboard redraws and
    any saved changes apply to the next snapshot.

## --no-leaf and --no-color

27. `sourcegraph-mcp dashboard --no-leaf`. Expected: brand mark is `[x]` instead of 🌿; section
    glyphs are bracketed-ASCII tokens.
28. `sourcegraph-mcp dashboard --no-color`. Expected: same layout, no ANSI colour codes (verify
    by piping output through `cat -v` is not applicable for live TUI — eyeball the colours).
29. `SOURCEGRAPH_NO_LEAF=1 sourcegraph-mcp dashboard`. Same as #27.
30. `NO_COLOR=1 sourcegraph-mcp dashboard`. Same as #28.

## Exit codes

31. `sourcegraph-mcp dashboard ; echo $?` after `q` → `0`.
32. `sourcegraph-mcp dashboard` against a tiny terminal → exit `2` (covered by #7).
33. Trigger an uncaught exception (e.g. corrupt the active `.sourcegraph/_meta.db` so a
    rebuild trips). Expected: exit `1`, cursor restored, single-line error on stderr.

Pass = every line item above behaves as described. Sign off in the merge thread.

-- Views.sql — stable agent-facing view layer over the per-scope SQLite tables.
-- Substituted at connection-setup time by MultiScopeReadOnlyConnection.
-- The placeholder tokens below (each beginning with double-brace) are replaced with one
-- per-scope SELECT branch joined by UNION ALL. See
-- openspec/changes/add-graph-query/design.md Decision 2 for the full contract.
-- (Tokens are not shown here verbatim to avoid being mistaken for substitution targets;
-- the {{SCOPE_UNION_BLOCK_<view>}}-style placeholders appear only in the view bodies below.)
--
-- Naming notes (renames vs. underlying tables):
--   * v_symbols.start_column / end_column are exposed as start_column / end_column
--     for ergonomics, even though the underlying table uses start_col / end_col.
--   * v_references.column_number renames the underlying refs.col — SQL reserves the
--     bare identifier COLUMN, so we avoid the double-quoting tax for agent queries.
--   * v_files.sha renames files.content_sha256 — "sha" is the agent-facing name.
--   * v_edges.kind / v_symbols.kind are projected from the underlying *_kind_name
--     columns; "kind" is the contract.
--   * v_references.kind maps the integer Core.ReferenceKind enum to short text:
--     0='def', 1='ref', 2='call', 3='impl', 4='inherit', 5='read', 6='write'.

CREATE TEMP VIEW v_symbols AS
{{SCOPE_UNION_BLOCK_v_symbols}}
;

CREATE TEMP VIEW v_files AS
{{SCOPE_UNION_BLOCK_v_files}}
;

CREATE TEMP VIEW v_edges AS
{{SCOPE_UNION_BLOCK_v_edges}}
;

CREATE TEMP VIEW v_references AS
{{SCOPE_UNION_BLOCK_v_references}}
;

CREATE TEMP VIEW v_scopes AS
SELECT
  s.id              AS scope,
  s.name            AS name,
  s.root            AS root,
  s.isolated        AS isolated,
  s.status          AS status,
  s.last_indexed_at AS last_indexed_at
FROM meta.scopes s;

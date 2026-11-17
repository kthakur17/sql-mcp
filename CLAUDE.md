# sql-mcp — notes for Claude Code

A .NET 10 MCP server (stdio) that gives AI agents read-only access to SQL Server. The product *is* a
safety boundary, so a change that weakens a check is a security bug even when every test passes.
Read [docs/SECURITY.md](docs/SECURITY.md) and the ADRs in [docs/adr/](docs/adr/) before touching
anything under `src/SqlMcp/Safety/` or `src/SqlMcp/Data/QueryExecutor.cs`.

## Commands

```bash
dotnet build                 # TreatWarningsAsErrors is on: a warning fails the build
dotnet test                  # unit + integration + end-to-end MCP tests (xUnit v3, Microsoft.Testing.Platform)
dotnet pack src/SqlMcp -c Release -o ./artifacts
```

Integration and end-to-end tests use LocalDB by default, or the server in `SQLMCP_TEST_SERVER`.
Locally they skip when no server is reachable; in CI they must run, so a "skipped" locally is not a pass.

## Layout

| Path | What lives there |
|---|---|
| `src/SqlMcp/Safety/QueryValidator.cs` | ScriptDom parse; accepts exactly one `SelectStatement` |
| `src/SqlMcp/Safety/AccessPolicy.cs`, `GlobPattern.cs` | schema allow-list, table deny-list, sensitive-column patterns |
| `src/SqlMcp/Data/` | `QueryExecutor` (rolled-back transaction, timeout, row cap, redaction), `SchemaCatalog`, `PlanExplainer` |
| `src/SqlMcp/Tools/DatabaseTools.cs` | the five MCP tools — the only public surface |
| `src/SqlMcp/Auditing/AuditLog.cs` | JSON-lines audit of every call, including rejected ones |
| `tests/SqlMcp.Tests/{Safety,Data}` | unit tests |
| `tests/SqlMcp.Tests/Integration` | real-database and stdio end-to-end tests |

## Rules

1. **Parse, never pattern-match SQL.** New checks walk the ScriptDom tree (ADR 0001). No regex or
   `Contains` on query text.
2. **Every layer stays.** Validator, policy, always-rolled-back transaction and row cap are each
   assumed to be imperfect; do not remove one because another "already covers it" (ADR 0002).
3. **Every new rejection gets a test** in `QueryValidatorTests` or `AccessPolicyTests` — one allowed
   case and at least one hostile case (comments, string literals, `GO`, nested CTE/subquery).
4. **Denied and missing tables must be indistinguishable** — same error text, absent from listings and
   foreign keys.
5. **Rejections explain themselves.** An agent reads the message and retries, so say what to change.
6. **Tools stay read-only** and keep `readOnlyHint: true`. A write-capable tool does not belong here.
7. **No secrets in the repo.** Connection strings come from `--config`, env vars or arguments. The CI
   `sa` password is for a throwaway container only.
8. Style is enforced by `.editorconfig` and analyzers: file-scoped namespaces, braces on every block,
   `_camelCase` private fields, nullable enabled.

## Workflow

- For anything touching more than one file, state the plan first and wait for a go-ahead.
- Run `dotnet build` and `dotnet test` after every change and report the real result, including skips.
- Update `README.md` / `docs/SECURITY.md` in the same change when behaviour or a known limit changes;
  add an ADR for a decision someone would later ask "why?" about.
- Never commit or push without being asked. Work on a branch; changes reach `main` through a reviewed PR.

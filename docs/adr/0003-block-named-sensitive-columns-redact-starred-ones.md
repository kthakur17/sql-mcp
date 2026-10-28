# 0003. Block named sensitive columns, redact starred ones

- Status: accepted
- Date: 2026-10-28

## Context

Some columns, such as password hashes, API tokens and national ID numbers, should never reach a model. Redacting by result-column name alone is easy to get around:

- `SELECT PasswordHash AS x` renames the column;
- `SELECT UPPER(PasswordHash)` turns it into an expression with no source column;
- `WHERE PasswordHash LIKE 'a%'` never returns the value but leaks it one character at a time through row counts.

Blocking every query that touches a table with a sensitive column would make tables like `Users` unusable.

## Decision

Use two mechanisms, one for each way a column can appear:

1. **Named references are blocked by the validator.** Any `ColumnReferenceExpression` whose name matches a sensitive pattern rejects the query, wherever it appears: select list, `WHERE`, `ORDER BY`, a join, or a subquery. Aliases, expressions and filter-based probing all need the column's name, so all are closed.
2. **Starred references are redacted by the executor.** `SELECT *` and `t.*` never name the column. The executor reads with `CommandBehavior.KeyInfo`, which makes SQL Server report each result column's base column, so a column is redacted when either its result name or its base name matches. Hidden key columns that `KeyInfo` adds are dropped.

Built-in patterns cover common credential names. Configured patterns add to them and never replace them, so a config mistake can't switch the protection off.

## Consequences

- An alias that happens to look sensitive (`SELECT Name AS token`) is rejected. That false positive is cheap, and the error message says how to fix it.
- A view that renames a sensitive column defeats both mechanisms, because no name in the query or the metadata matches. This is documented in [SECURITY.md](../SECURITY.md); the fix belongs in the database (`DENY SELECT` on the column).

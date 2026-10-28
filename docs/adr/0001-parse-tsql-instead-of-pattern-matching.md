# 0001. Parse T-SQL with ScriptDom instead of pattern matching

- Status: accepted
- Date: 2026-10-28

## Context

The server has to decide whether an arbitrary string from an agent is a single read-only query. The usual first attempt is a keyword deny-list: refuse text containing `INSERT`, `DELETE`, `DROP` and so on. That approach fails in both directions:

- **It lets bad queries through.** `SELECT 1 /* ; */ ; /**/ DELETE dbo.Orders`, a `GO` on its own line, and Unicode or bracket-quoted tricks all need special cases. Every special case is a regex someone has to get exactly right.
- **It blocks good queries.** `SELECT 'DELETE FROM x' AS Text`, a column named `UpdatedAt`, and a comment mentioning `DROP` all fail.

## Decision

Parse with `Microsoft.SqlServer.TransactSql.ScriptDom` (`TSql170Parser`), the parser behind SQL Server's own tooling, and decide on the syntax tree:

- reject any parse error;
- require exactly one statement across all batches, and that it is a `SelectStatement` with no `INTO`;
- walk the tree with a `TSqlFragmentVisitor` to reject external rowsets, linked-server and cross-database names, `NEXT VALUE FOR`, blocking table hints and sensitive column references, and to apply the table policy to every `NamedTableReference` and table-valued function. CTE names are collected first so that they aren't mistaken for tables.

## Consequences

- Comments and string literals are not tokens the checks look at, so they need no handling.
- The decision is only as good as the visitor's coverage of the grammar. New syntax in a future SQL Server version would need a parser update. Rolling back every transaction ([ADR 0002](0002-always-rolled-back-transaction.md)) and the database's permissions back this up.
- The parser is the largest dependency, at about 6.6 MB of the 7 MB tool package. That's a fair price for a server installed once per machine.

# Security model

sql-mcp assumes the agent is helpful but fallible, and that a prompt injection hidden in the data it reads may try to steer it. The goal is that neither a confused agent nor a hostile instruction can change data, reach outside the database, or read what the policy hides.

## What is enforced

| Threat | Control |
|---|---|
| Writes, DDL, `EXEC`, `WAITFOR` | Only a single parsed `SelectStatement` is accepted |
| A second statement hidden after the first | Batches and statements are counted on the syntax tree, so comments, strings and `GO` don't matter |
| `SELECT ... INTO` creating a table | Rejected on the tree |
| Reaching other servers or files | `OPENROWSET`, `OPENQUERY`, `OPENDATASOURCE`, bulk rowsets and four-part names are rejected |
| Reading another database on the same server | Three-part names are rejected unless `AllowCrossDatabase` is on |
| Side effects inside a `SELECT` | `NEXT VALUE FOR` (advances a sequence) is rejected |
| Blocking other sessions | `TABLOCKX`, `UPDLOCK`, `HOLDLOCK`, `XLOCK` and similar hints are rejected; queries time out |
| Anything the parser missed | The query runs in a transaction that is always rolled back |
| Exhausting memory or the context window | Row cap with server-side cancel; long cells truncated |
| Credential columns | Named references blocked; `SELECT *` results redacted using SQL Server's base-column metadata |
| Probing for hidden tables | Denied and missing tables return the same error, and are filtered out of listings and foreign keys |
| Unreviewed agent activity | Every call is appended to a JSON-lines audit log, including rejected ones |
| An over-privileged login | Startup check warns on stderr; `server_info` reports the same warnings to the agent |

## Known limits

These are by design or not yet covered. The database's own permissions close each of them, which is why a least-privilege login (`scripts/create-readonly-user.sql`) is part of the setup, not an optional extra.

- **Views and synonyms can rename things.** A view that exposes `PasswordHash AS h` gets around column-name patterns, and a synonym can point at a denied table under another name. Use `DENY SELECT` on the column or object in the database.
- **Scalar functions are not inspected.** A user-defined function called from a `SELECT` runs with the login's permissions. A read-only login limits what it can do.
- **Unqualified names are resolved conservatively, not exactly.** sql-mcp can't know the caller's default schema, so a deny pattern on `hr.Salaries` also blocks an unqualified `Salaries` in any schema. With a schema allow-list configured, unqualified names are rejected outright.
- **Data reaches the model.** Whatever a query returns is sent to the agent's model provider. Redaction and deny lists decide what that is. sql-mcp does not classify data on its own.
- **Expensive reads are bounded, not prevented.** A query can use CPU up to the timeout. `explain_query` lets an agent check first; a cost gate is on the roadmap.
- **The audit log is local and append-only by convention.** It is not tamper-proof. Ship it to central logging if that matters.

## Reporting a vulnerability

Please open a private security advisory on the GitHub repository rather than a public issue.

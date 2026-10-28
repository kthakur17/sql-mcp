# 0002. Run every query in a transaction that is always rolled back

- Status: accepted
- Date: 2026-10-28

## Context

The validator ([ADR 0001](0001-parse-tsql-instead-of-pattern-matching.md)) is the main control, but a security design that depends on one component being bug-free is fragile. We want a second, independent layer that makes a write harmless even if one reaches the database.

Options considered:

- **`ApplicationIntent=ReadOnly`** only has an effect against an Availability Group readable secondary. On a single server it changes nothing.
- **Setting the database to `READ_ONLY`** affects every other user of the database.
- **Relying only on login permissions** is correct, and is recommended, but sql-mcp can't enforce it. It can only detect it and warn.

## Decision

`QueryExecutor` opens a `READ COMMITTED` transaction for every query and rolls it back in a `finally`, whether the query succeeded, failed or was cancelled. The integration suite proves this layer on its own: it bypasses the validator, runs an `INSERT` followed by a `SELECT`, and checks that the row count hasn't changed.

## Consequences

- A write that slipped past the validator would still be undone. It would, however, hold locks until the rollback, which happens within milliseconds.
- Effects outside the transaction are not covered: sequence values already handed out, identity gaps, and anything done through an external procedure. The validator rejects `NEXT VALUE FOR` for this reason, and the least-privilege login covers the rest.
- Wrapping a read in a transaction adds roughly one round trip of overhead per query.

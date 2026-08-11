# ADR 0002: Dialect-aware SQL parser validation

## Status

Accepted

## Context

CD-51 requires the Core to enforce read-only mode (Story 1.2) and table
visibility (Story 1.3) on every SQL statement before it executes, regardless of
which API surface (MCP, local named pipe, future REST) submitted it, and to
apply the same checks to LLM-generated SQL (Story 1.4). The risk called out in
the epic is that generated SQL can bypass visibility rules through subqueries,
CTEs, joins, or synonyms.

String matching or regex over raw SQL cannot reliably answer "what kind of
statement is this?" or "which tables does it touch?" — a hidden table can be
masked behind an alias, a CTE, or a join, and a write can be hidden in a
multi-statement batch. The two supported providers (SQL Server and PostgreSQL)
also have distinct dialects, so a single naive grammar would mis-parse one of
them.

## Decision

Validate every statement by parsing it into an AST before execution, using the
`SqlParserCS` library with the dialect that matches the connection's provider
(`MsSqlDialect` for SQL Server, `PostgreSqlDialect` for PostgreSQL). The parser
is the only thing that inspects SQL; policy code never touches raw text.

`SqlAnalyzer.Analyze` turns SQL into parsed statements that expose statement
kind (Read / Write / Other), the concrete statement type, normalized
(re-rendered) SQL, and every real table reference. A scope-aware AST walk
resolves CTE aliases so they are not mistaken for tables, while the real base
tables inside a CTE body are still surfaced — a hidden table cannot be masked by
wrapping it in a CTE or alias.

`SqlPolicyValidator.Validate` then fails closed, rejecting, before execution:

- unparseable SQL (treated as a denial, not an error),
- multi-statement batches,
- unsupported statement types (DDL, EXEC, TRUNCATE, etc.),
- mutating statements on a read-only connection,
- any reference to a table the connection hides.

Each outcome carries a stable deny code (`policy_denied_*`) for auditing and
client error mapping.

## Consequences

- Read-only and table-visibility enforcement is centralized in Core, shared by
  all API surfaces, and unit-testable against the AST rather than text.
- Provider-specific escape hatches — synonyms, views, security-definer
  functions — are not resolved by the parser and remain a known gap requiring
  explicit future handling (see the risks recorded against CD-51).
- Adding a third provider requires mapping a new dialect; an unmapped provider
  fails closed by throwing rather than running unvalidated SQL.
- The normalized SQL produced during parsing gives auditing a canonical form of
  what was checked.

## References

- `src/SqlAgent.Core/Policy/SqlPolicy.cs` — `SqlAnalyzer`, `SqlPolicyValidator`.
- `src/SqlAgent.Core/SqlAgent.Core.csproj` — `SqlParserCS` dependency.
- CD-51 Stories 1.2, 1.3, 1.4; CD-50 T5.

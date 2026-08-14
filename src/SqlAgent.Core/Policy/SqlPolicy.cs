using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace SqlAgent.Core.Policy;

/// <summary>
/// How a parsed statement relates to the read-only policy. The agent only ever runs read queries
/// and (on writable connections) basic DML; anything else is <see cref="Other"/> and denied.
/// </summary>
public enum SqlStatementKind
{
    /// <summary>SELECT — allowed on read-only and writable connections (visibility permitting).</summary>
    Read,

    /// <summary>INSERT / UPDATE / DELETE — allowed only when the connection is not read-only.</summary>
    Write,

    /// <summary>DDL, EXEC, TRUNCATE, etc. — never supported by the agent in v1 (fail closed).</summary>
    Other,
}

/// <summary>A table named by a statement. <see cref="Schema"/> is null when the SQL left it unqualified.</summary>
public record SqlTableReference(string? Schema, string Name)
{
    public override string ToString() => Schema is null ? Name : $"{Schema}.{Name}";
}

/// <summary>
/// One parsed statement: its kind, the parser's concrete type name, its canonical re-rendered
/// (normalized) SQL, every table it touches, and the subset of those it writes to.
/// <paramref name="WrittenTables"/> is always a subset of <paramref name="Tables"/>, so a visibility
/// check over <paramref name="Tables"/> still sees write targets exactly as it did before this existed.
/// For a <see cref="SqlStatementKind.Write"/> statement it is never empty — see
/// <see cref="SqlAnalyzer"/> for why an unidentifiable target falls back to everything.
/// </summary>
public record ParsedStatement(
    SqlStatementKind Kind,
    string StatementType,
    string Normalized,
    IReadOnlyList<SqlTableReference> Tables,
    IReadOnlyList<SqlTableReference> WrittenTables);

/// <summary>
/// Dialect-aware SQL parsing (ADR-0002, CD-50 T5). Turns raw SQL into <see cref="ParsedStatement"/>s
/// exposing statement type and referenced objects, so policy checks never touch the raw text with regex.
/// </summary>
public static class SqlAnalyzer
{
    /// <summary>
    /// Parses <paramref name="sql"/> with the dialect for <paramref name="provider"/>. A batch may yield
    /// several statements (the caller rejects multi-statement input). Throws <see cref="ParserException"/>
    /// on invalid SQL; callers treat that as a fail-closed denial.
    /// </summary>
    public static IReadOnlyList<ParsedStatement> Analyze(string sql, DatabaseProviderType provider)
    {
        var statements = new Parser().ParseSql(sql, DialectFor(provider));
        return statements.Select(Describe).ToList();
    }

    private static Dialect DialectFor(DatabaseProviderType provider) => provider switch
    {
        DatabaseProviderType.SqlServer => new MsSqlDialect(),
        DatabaseProviderType.Postgres => new PostgreSqlDialect(),
        _ => throw new NotSupportedException($"No SQL dialect mapped for provider {provider}."),
    };

    private static ParsedStatement Describe(Statement statement)
    {
        var collector = new TableCollector();
        collector.Walk(statement, ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase));

        var kind = statement switch
        {
            Statement.Select => SqlStatementKind.Read,
            Statement.Insert or Statement.Update or Statement.Delete => SqlStatementKind.Write,
            _ => SqlStatementKind.Other,
        };

        // A write can hide behind a syntactically read-shaped wrapper. `WITH cte AS (...) INSERT INTO t
        // SELECT ...` parses as a top-level Statement.Select — the INSERT lives nested inside the query
        // body (Query.Body is a SetExpression.Insert wrapping a real Statement.Insert) — and so does
        // `SELECT ... INTO t`, which is a Select node all the way down and writes anyway. The switch
        // above alone would call either a Read and let it skip the read-only connection gate. Rather
        // than enumerate every SqlParserCS wrapper shape that could carry a nested write (a list a future
        // parser version would silently outdate), trust the collector's write set instead: if it found a
        // target while walking this statement, something in it writes, whatever the outer node looked
        // like. This can only push a statement from Read to Write, never the other way, so it cannot turn
        // a real write into something that reads as safe. Other is left alone — it is already denied
        // unconditionally regardless of Kind, so upgrading it would change nothing but the label.
        if (kind == SqlStatementKind.Read && collector.WrittenReferences.Count > 0)
            kind = SqlStatementKind.Write;

        // Fail closed. The collector finds a write target by walking the AST property that holds it, and
        // an upgrade of SqlParserCS that renames or restructures that property would leave the set empty
        // — which the per-object check would read as "this statement writes to nothing" and wave through.
        // Falling back to every referenced table makes the same upgrade over-strict instead: a write
        // touching a Read-only object is refused, which is loud, recoverable, and the right direction.
        var written = collector.WrittenReferences;
        if (kind == SqlStatementKind.Write && written.Count == 0)
            written = collector.References;

        return new ParsedStatement(
            kind, statement.GetType().Name, statement.ToSql(), collector.References, written);
    }

    /// <summary>
    /// Walks the parsed AST collecting real table references, scope-aware about CTEs. The SqlParserCS
    /// visitor is flat (no <c>Query</c> hook) so it cannot track which CTE names are in scope where; this
    /// generic walk does. A <c>WITH</c> clause's names are in scope for the query body (and every
    /// descendant), so <c>FROM &lt;cte&gt;</c> resolves to the alias and is dropped — but a real table of
    /// the same name in an outer or sibling scope keeps being checked. CTE *bodies* see a narrower scope:
    /// a non-recursive CTE cannot see its own name (so <c>WITH t AS (SELECT * FROM t)</c> reads the real
    /// table <c>t</c>), only the names of CTEs declared before it. Schema-qualified names are never CTEs
    /// and always stay. Catches tables behind aliases, joins, CTE bodies, subqueries, and DML targets in
    /// one pass.
    /// </summary>
    private sealed class TableCollector
    {
        private readonly List<SqlTableReference> _refs = [];
        private readonly List<SqlTableReference> _written = [];
        private readonly HashSet<(string?, string)> _seen = [];
        private readonly HashSet<(string?, string)> _seenWritten = [];
        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);

        public IReadOnlyList<SqlTableReference> References => _refs;
        public IReadOnlyList<SqlTableReference> WrittenReferences => _written;

        public void Walk(object? node, ImmutableHashSet<string> scope, bool writeTarget = false)
        {
            if (node is null or string) return;
            if (node is IEnumerable sequence)
            {
                foreach (var item in sequence) Walk(item, scope, writeTarget);
                return;
            }
            if (node.GetType().Namespace?.StartsWith("SqlParser.Ast", StringComparison.Ordinal) != true) return;
            if (!_visited.Add(node)) return;

            // Real table references: FROM/JOIN targets, plus UPDATE/DELETE targets (also TableFactor.Table).
            if (node is TableFactor.Table table)
                AddUnlessCte(table.Name, scope, writeTarget);
            // INSERT target is a bare ObjectName, not a TableFactor, so pick it up explicitly — and it is
            // unambiguously the write target, whatever flag the walk arrived with.
            else if (node is Statement.Insert insert)
                AddUnlessCte(insert.InsertOperation.Name, scope, writeTarget: true);
            // `SELECT ... INTO t` creates t and fills it. Both dialects parse it as a Statement.Select
            // whose target hangs off Select.Into — neither a TableFactor nor an Insert — so without this
            // branch it reported Kind = Read with an empty write set and ran on a read-only connection.
            else if (node is SelectInto into)
                AddUnlessCte(into.Name, scope, writeTarget: true);

            // A WITH clause needs per-part scoping, so handle a Query's CTEs explicitly rather than letting
            // the generic property walk apply one flat scope to both the bodies and the CTE definitions.
            if (node is Query { With: { } with } query)
            {
                WalkQueryWithCtes(query, with, scope);
                return;
            }

            // UPDATE and DELETE name their target as an ordinary TableFactor, indistinguishable from the
            // read sources beside it in a FROM or USING clause. The only thing that separates them is
            // which property of the statement they hang off, so that property is walked first with the
            // flag set; _visited then keeps the generic walk below from revisiting it as a read source.
            // An UPDATE's target is walked as a whole subtree, which over-marks if a joined relation ever
            // appears under it — not valid in either supported dialect, and the fail-closed answer if it
            // becomes so. A DELETE's is not: T-SQL's `DELETE o FROM orders o JOIN lookup x` puts genuine
            // read sources in the same clause, and marking them written would make a Read-only lookup
            // table refuse a legitimate join-qualified DELETE — the exact toxicity the write/read split
            // exists to prevent.
            foreach (var target in WriteTargets(node))
                Walk(target, scope, writeTarget: true);

            foreach (var value in ChildNodes(node))
                Walk(value, scope, writeTarget);
        }

        /// <summary>
        /// The node (or nodes) a statement writes to, walked with the write flag set. The property holding
        /// it is found by name rather than by a type test, because SqlParserCS offers no marker for "this
        /// is the thing being modified", and the shape differs between an UPDATE and a DELETE:
        /// <c>Statement.Update.Table</c> holds it directly, but a DELETE's target sits two levels down, at
        /// <c>DeleteOperation.From</c> — this case is keyed on <see cref="DeleteOperation"/> rather than
        /// <see cref="Statement.Delete"/> so it fires when the generic walk reaches that nested node on
        /// its own. A name that stops matching after a package upgrade yields nothing here, which
        /// <see cref="SqlAnalyzer.Describe"/> turns into the fail-closed fallback rather than into silent
        /// permission; a *shape* that stops matching falls back to the whole clause, which over-marks
        /// rather than under-marks.
        /// </summary>
        private IEnumerable<object?> WriteTargets(object node)
        {
            switch (node)
            {
                case Statement.Update:
                {
                    var table = Property(node, "Table");
                    if (table is null) yield break;
                    yield return ResolveUpdateAlias(table, Property(node, "From")) ?? table;
                    yield break;
                }

                case DeleteOperation deletion:
                {
                    var from = Property(node, "From");
                    if (from is null) yield break;

                    // FromTable.WithFromKeyword and .WithoutKeyword both wrap the entry list in `From`.
                    var targets = DeleteTargets(deletion, Property(from, "From")).ToList();
                    if (targets.Count == 0)
                    {
                        yield return from;
                        yield break;
                    }
                    foreach (var target in targets) yield return target;
                    yield break;
                }
            }
        }

        /// <summary>
        /// Which relations of a DELETE's FROM clause are actually deleted from. Two forms:
        ///
        /// T-SQL's <c>DELETE x FROM orders o JOIN lookup x ON ...</c> names its target ahead of the FROM
        /// clause, in <c>DeleteOperation.Tables</c>, by alias where one is declared and by table name
        /// where none is. It is not necessarily the first relation — that example deletes from
        /// <c>lookup</c> — so the name is resolved against the clause rather than positionally, and the
        /// relations it does not name stay reads.
        ///
        /// Plain <c>DELETE FROM a</c> (and the comma form <c>DELETE FROM a, b</c>) names nothing, so every
        /// comma-separated entry is a target — but not the relations joined onto them, which are reads.
        ///
        /// Returning nothing means the shape was not recognised, and the caller falls back to marking the
        /// whole clause written: coarse, and in the safe direction.
        /// </summary>
        private static IEnumerable<object?> DeleteTargets(DeleteOperation deletion, object? entries)
        {
            if (entries is null) return [];

            if (deletion.Tables is { Count: > 0 } named)
            {
                var relations = ClauseRelations(entries).ToList();
                var resolved = new List<object?>();
                foreach (var name in named)
                {
                    var match = MatchRelation(relations, name);
                    if (match is null) return [];
                    resolved.Add(match);
                }
                return resolved;
            }

            return ClauseEntries(entries)
                .Select(e => e.Relation)
                .Where(r => r is not null)
                .Cast<object?>();
        }

        /// <summary>
        /// The relation a bare name in <c>DELETE &lt;name&gt; FROM ...</c> refers to. A relation that
        /// declares an alias is addressable only by that alias, so the alias is what is compared when one
        /// is present and the table name when it is not — the rule the engines themselves apply.
        /// </summary>
        private static TableFactor.Table? MatchRelation(IEnumerable<TableFactor> relations, ObjectName name)
        {
            if (name.Values.Count == 0) return null;
            var wanted = name.Values[^1].Value;

            foreach (var relation in relations)
            {
                if (relation is not TableFactor.Table candidate) continue;
                var addressable = candidate.Alias?.Name.Value ?? candidate.Name.Values[^1].Value;
                if (string.Equals(addressable, wanted, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// T-SQL's canonical update-with-join idiom — <c>UPDATE o SET ... FROM orders o</c>, which both
        /// supported dialects parse — puts the *alias* in <c>Update.Table</c> and the real table only in
        /// <c>Update.From</c>. Recording the alias hands the policy a name no object has, so it resolves
        /// to full access and is not a view, and the write reaches an engine that applies it to the real
        /// table. Resolve it instead: when the target is a bare, unaliased, single-part name matching an
        /// alias declared among the FROM clause's own relations, that relation is what is written.
        ///
        /// Only the FROM clause's top-level relations and its joined ones are searched — an alias declared
        /// inside a derived table is not in scope for the UPDATE target — and only an exact alias match
        /// resolves. Anything else returns null and the caller records <c>Update.Table</c> as before, so an
        /// unfamiliar shape costs precision, never enforcement.
        /// </summary>
        private object? ResolveUpdateAlias(object updateTarget, object? from)
        {
            if (from is null) return null;
            if (updateTarget is not TableWithJoins { Relation: TableFactor.Table { Alias: null } named })
                return null;
            if (named.Name.Values.Count != 1) return null;
            var alias = named.Name.Values[0].Value;

            foreach (var relation in ClauseRelations(from))
            {
                if (relation is not TableFactor.Table { Alias: { } declared } real) continue;
                if (!string.Equals(declared.Name.Value, alias, StringComparison.OrdinalIgnoreCase)) continue;

                // The alias names no object, so it must not reach the reference list either. Marking it
                // visited is how it is dropped: the generic walk skips anything already seen.
                _visited.Add(named);
                return real;
            }

            return null;
        }

        /// <summary>
        /// The relations a FROM-style clause names directly: each comma-separated entry and everything
        /// joined onto it. Nothing nested inside a derived table or a subquery — those are read sources in
        /// their own right and are reached by the ordinary walk.
        /// </summary>
        private static IEnumerable<TableFactor> ClauseRelations(object clause)
        {
            foreach (var entry in ClauseEntries(clause))
            {
                if (entry.Relation is { } relation) yield return relation;
                if (entry.Joins is null) continue;
                foreach (var join in entry.Joins)
                    if (join.Relation is { } joined) yield return joined;
            }
        }

        /// <summary>The comma-separated entries of a FROM-style clause, whatever wrapper holds them.</summary>
        private static IEnumerable<TableWithJoins> ClauseEntries(object clause) => clause switch
        {
            TableWithJoins one => [one],
            IEnumerable<TableWithJoins> many => many,
            _ => [],
        };

        private static object? Property(object node, string name)
        {
            var prop = node.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null || prop.GetIndexParameters().Length > 0) return null;
            try { return prop.GetValue(node); }
            catch { return null; }
        }

        private void WalkQueryWithCtes(Query query, With with, ImmutableHashSet<string> outerScope)
        {
            var allNames = with.CteTables.Select(c => c.Alias.Name.Value).ToList();
            var bodyScope = outerScope.Union(allNames);

            // Walk each CTE definition with only the names visible to it: prior CTEs (non-recursive), or
            // the whole WITH list including itself (recursive, where self/mutual references are legal).
            var priorScope = outerScope;
            foreach (var cte in with.CteTables)
            {
                Walk(cte.Query, with.Recursive ? bodyScope : priorScope);
                if (!with.Recursive)
                    priorScope = priorScope.Add(cte.Alias.Name.Value);
            }

            // Walk the rest of the query (body, ORDER BY, LIMIT, ...) with all CTE names in scope.
            foreach (var prop in query.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name == nameof(Query.With)) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                object? value;
                try { value = prop.GetValue(query); }
                catch { continue; }
                Walk(value, bodyScope);
            }
        }

        private static IEnumerable<object?> ChildNodes(object node)
        {
            foreach (var prop in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0) continue;
                object? value;
                try { value = prop.GetValue(node); }
                catch { continue; }
                yield return value;
            }
        }

        private void AddUnlessCte(ObjectName name, ImmutableHashSet<string> scope, bool writeTarget)
        {
            // Identifier values are already unquoted ([dbo].[T] -> dbo, T). Last part is the table, the
            // part before it (if any) is the schema; deeper qualifiers (db.schema.table) are ignored.
            var parts = name.Values.Select(i => i.Value).ToList();
            if (parts.Count == 0) return;
            var tableName = parts[^1];
            var schema = parts.Count >= 2 ? parts[^2] : null;

            // Unqualified name matching an in-scope CTE is an alias, not a table — skip it, but only on
            // the read side. Neither supported engine lets you write into a WITH alias: an INSERT/UPDATE/
            // DELETE target resolves against the catalog, not the WITH list, so a write target whose name
            // happens to shadow a CTE cannot actually be the CTE — it names the real base table of the
            // same name. Dropping it here, the way a read source is dropped, would silently take that
            // table out of both Tables and WrittenTables and let the write skip the read-only gate and the
            // per-object policy entirely. So a write target always reaches both lists under its real name,
            // whatever it happens to be shadowing.
            if (!writeTarget && schema is null && scope.Contains(tableName)) return;

            var reference = new SqlTableReference(schema, tableName);
            if (_seen.Add((schema, tableName)))
                _refs.Add(reference);
            if (writeTarget && _seenWritten.Add((schema, tableName)))
                _written.Add(reference);
        }
    }
}

/// <summary>
/// Allow/deny outcome with a stable code (for audit + client error mapping), the tables seen, and the
/// normalized SQL (canonical re-render of the single parsed statement; null when SQL was empty/unparseable).
/// </summary>
public record PolicyDecision(
    bool Allowed,
    string? DenyCode,
    string? Reason,
    IReadOnlyList<SqlTableReference> ReferencedTables,
    string? NormalizedSql = null)
{
    public static PolicyDecision Allow(IReadOnlyList<SqlTableReference> tables, string? normalizedSql) =>
        new(true, null, null, tables, normalizedSql);

    public static PolicyDecision Deny(
        string code, string reason, IReadOnlyList<SqlTableReference> tables, string? normalizedSql = null)
        => new(false, code, reason, tables, normalizedSql);
}

/// <summary>
/// What a connection's policy allows against one object. Ordered from most to least restrictive so a
/// caller resolving an ambiguous (unqualified) name can take the minimum and stay fail-closed.
/// </summary>
public enum ObjectAccess
{
    /// <summary>Not visible: absent from the schema handed to the model, and refused if named anyway.</summary>
    Hidden = 0,

    /// <summary>Visible and readable; refused as the target of a write.</summary>
    ReadOnly = 1,

    /// <summary>Visible, readable, writable. Also what an object with no policy row gets.</summary>
    Full = 2,
}

/// <summary>
/// One object's effective policy: its access level, and whether it is a view. Both travel together
/// because a caller resolving a name has to do exactly one lookup, and doing it twice is how the
/// fail-closed matching rule for unqualified names ends up implemented two slightly different ways.
/// </summary>
public record ObjectPolicy(ObjectAccess Access, bool IsView);

/// <summary>
/// Applies connection policy to parsed SQL (CD-50 T5): rejects multi-statement batches, unsupported
/// statements, mutating statements on read-only connections, references to hidden objects, writes to
/// views, and writes to read-only objects — all before execution. Decoupled from storage: policy is
/// supplied as one resolver.
/// </summary>
public static class SqlPolicyValidator
{
    /// <param name="resolve">
    /// Returns the effective policy for one referenced object. An object with no policy row must resolve
    /// to <see cref="ObjectAccess.Full"/> and <c>IsView: false</c> — absence means full access, which is
    /// the rule every layer in this codebase applies. Only real objects reach this resolver: CTE aliases
    /// are resolved out scope-aware during parsing, as is the alias an UPDATE names its target by, while
    /// the real base tables inside a CTE body are still passed in — so a hidden object cannot be masked
    /// by wrapping it in a CTE or standing an alias in front of it.
    ///
    /// The caller owns the fail-closed rule for an unqualified name — take the most restrictive
    /// <see cref="ObjectAccess"/> among same-named objects across schemas, and report a view if any match
    /// is one.
    /// </param>
    public static PolicyDecision Validate(
        string sql,
        DatabaseProviderType provider,
        bool isReadOnly,
        Func<SqlTableReference, ObjectPolicy> resolve)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return PolicyDecision.Deny("policy_denied_empty", "No executable SQL statement was provided.", []);

        IReadOnlyList<ParsedStatement> statements;
        try
        {
            statements = SqlAnalyzer.Analyze(sql, provider);
        }
        catch (ParserException ex)
        {
            return PolicyDecision.Deny("policy_denied_parse_error", $"SQL could not be parsed: {ex.Message}", []);
        }

        if (statements.Count == 0)
            return PolicyDecision.Deny("policy_denied_empty", "No executable SQL statement was provided.", []);

        if (statements.Count > 1)
        {
            var all = statements.SelectMany(s => s.Tables).ToList();
            return PolicyDecision.Deny(
                "policy_denied_multi_statement",
                $"Multi-statement batches are not allowed ({statements.Count} statements found).",
                all);
        }

        var stmt = statements[0];

        if (stmt.Kind == SqlStatementKind.Other)
            return PolicyDecision.Deny(
                "policy_denied_unsupported",
                $"Statement type '{stmt.StatementType}' is not supported.",
                stmt.Tables, stmt.Normalized);

        if (isReadOnly && stmt.Kind != SqlStatementKind.Read)
            return PolicyDecision.Deny(
                "policy_denied_readonly",
                $"Connection is read-only; '{stmt.StatementType}' would modify data.",
                stmt.Tables, stmt.Normalized);

        // Visibility first, and over every reference rather than only the written ones. A hidden object's
        // name must not leak, and refusing it for any more specific reason would concede that it exists.
        var hidden = stmt.Tables.Where(t => resolve(t).Access == ObjectAccess.Hidden).ToList();
        if (hidden.Count > 0)
            return PolicyDecision.Deny(
                "policy_denied_hidden_table",
                $"References table(s) not visible to this connection: {string.Join(", ", hidden)}.",
                stmt.Tables, stmt.Normalized);

        // Views before levels. A view can only be Not visible or Read-only, so checking the level first
        // would make this branch unreachable and would tell the user to raise a level that the object is
        // not allowed to have.
        var writtenViews = stmt.WrittenTables.Where(t => resolve(t).IsView).ToList();
        if (writtenViews.Count > 0)
            return PolicyDecision.Deny(
                "policy_denied_view_write",
                $"'{stmt.StatementType}' writes to view(s): {string.Join(", ", writtenViews)}.",
                stmt.Tables, stmt.Normalized);

        var readOnlyTargets = stmt.WrittenTables
            .Where(t => resolve(t).Access == ObjectAccess.ReadOnly)
            .ToList();
        if (readOnlyTargets.Count > 0)
            return PolicyDecision.Deny(
                "policy_denied_readonly_object",
                $"'{stmt.StatementType}' writes to read-only object(s): {string.Join(", ", readOnlyTargets)}.",
                stmt.Tables, stmt.Normalized);

        return PolicyDecision.Allow(stmt.Tables, stmt.Normalized);
    }
}

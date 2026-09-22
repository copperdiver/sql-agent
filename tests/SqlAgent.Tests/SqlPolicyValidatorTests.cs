using SqlAgent.Core;
using SqlAgent.Core.Policy;

namespace SqlAgent.Tests;

public class SqlPolicyValidatorTests
{
    // Default: every object fully accessible. Individual tests override.
    private static Func<SqlTableReference, ObjectPolicy> Policy(
        string[]? hidden = null, string[]? readOnly = null, string[]? views = null)
    {
        var hiddenSet = (hidden ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var readOnlySet = (readOnly ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var viewSet = (views ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return t =>
        {
            bool Match(HashSet<string> set) => set.Contains(t.Name) || set.Contains(t.ToString());
            var access = Match(hiddenSet) ? ObjectAccess.Hidden
                : Match(readOnlySet) ? ObjectAccess.ReadOnly
                : ObjectAccess.Full;
            return new ObjectPolicy(access, Match(viewSet));
        };
    }

    private static PolicyDecision Validate(
        string sql,
        bool isReadOnly = false,
        DatabaseProviderType provider = DatabaseProviderType.Postgres,
        Func<SqlTableReference, ObjectPolicy>? resolve = null,
        AllowedDdl allowedDdl = AllowedDdl.None,
        bool confirmed = true)
        => SqlPolicyValidator.Validate(sql, provider, isReadOnly, resolve ?? Policy(), allowedDdl, confirmed);

    [Theory]
    [InlineData("CREATE TABLE archive (id int)", DatabaseProviderType.Postgres, DdlOperation.CreateTable)]
    [InlineData("ALTER TABLE archive ADD COLUMN note text", DatabaseProviderType.Postgres, DdlOperation.AlterTable)]
    [InlineData("ALTER TABLE archive ADD note int", DatabaseProviderType.SqlServer, DdlOperation.AlterTable)]
    [InlineData("DROP TABLE archive", DatabaseProviderType.Postgres, DdlOperation.DropTable)]
    [InlineData("CREATE INDEX ix_archive_id ON archive (id)", DatabaseProviderType.Postgres, DdlOperation.CreateIndex)]
    [InlineData("DROP INDEX ix_archive_id", DatabaseProviderType.Postgres, DdlOperation.DropIndex)]
    [InlineData("TRUNCATE TABLE archive", DatabaseProviderType.Postgres, DdlOperation.Truncate)]
    public void Supported_ddl_gets_a_specific_operation(
        string sql, DatabaseProviderType provider, DdlOperation operation)
    {
        var parsed = Assert.Single(SqlAnalyzer.Analyze(sql, provider));

        Assert.Equal(SqlStatementKind.Ddl, parsed.Kind);
        Assert.Equal(operation, parsed.DdlOperation);
    }

    [Theory]
    [InlineData("CREATE VIEW report AS SELECT 1")]
    [InlineData("GRANT SELECT ON orders TO app")]
    [InlineData("EXEC report")]
    public void Unsupported_statement_shapes_remain_other_and_fail_closed(string sql)
    {
        var parsed = Assert.Single(SqlAnalyzer.Analyze(sql, DatabaseProviderType.Postgres));

        Assert.Equal(SqlStatementKind.Other, parsed.Kind);
        Assert.Equal(DdlOperation.Unsupported, parsed.DdlOperation);
    }

    // --- Read-only enforcement -------------------------------------------------

    [Theory]
    [InlineData("UPDATE orders SET total = 0")]
    [InlineData("INSERT INTO orders (id) VALUES (1)")]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("DROP TABLE orders")]
    [InlineData("TRUNCATE TABLE orders")]
    [InlineData("CREATE TABLE t (id int)")]
    public void ReadOnly_denies_mutating_and_ddl(string sql)
    {
        var d = Validate(sql, isReadOnly: true);
        Assert.False(d.Allowed);
        Assert.Contains(d.DenyCode, new[] { "policy_denied_readonly", "policy_denied_unsupported", "policy_denied_ddl" });
    }

    [Fact]
    public void ReadOnly_denies_a_write_wrapped_in_a_cte()
    {
        // WITH cte AS (...) INSERT ... parses as a top-level Statement.Select, so without the write-set
        // upgrade to Kind this would slip past the read-only gate as an ordinary read. This is the layer
        // that proves the gate is actually closed, not just that WrittenTables is populated correctly.
        var d = Validate(
            "WITH recent AS (SELECT id FROM orders) INSERT INTO archive SELECT id FROM recent",
            isReadOnly: true);

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_readonly", d.DenyCode);
    }

    [Fact]
    public void ReadOnly_denies_a_write_wrapped_in_a_cte_shadowing_its_own_target()
    {
        // WITH orders AS (...) INSERT INTO orders ... names a CTE the same as its own write target. An
        // INSERT target resolves against the catalog, not the WITH list, so this still writes the real
        // orders table — the CTE short-circuit that drops a shadowed read source must not swallow the
        // write target too, or this would pass the read-only gate as a no-op read of nothing.
        var d = Validate("WITH orders AS (SELECT 1) INSERT INTO orders SELECT 1", isReadOnly: true);

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_readonly", d.DenyCode);
    }

    [Fact]
    public void ReadOnly_allows_select()
    {
        var d = Validate("SELECT id FROM orders", isReadOnly: true);
        Assert.True(d.Allowed);
    }

    [Fact]
    public void Writable_allows_dml()
    {
        var d = Validate("UPDATE orders SET total = 0", isReadOnly: false);
        Assert.True(d.Allowed);
    }

    // --- Unsupported statements (fail closed, even on writable) -----------------

    [Theory]
    [InlineData("DROP TABLE orders")]
    [InlineData("ALTER TABLE orders ADD c int")]
    [InlineData("TRUNCATE TABLE orders")]
    public void Unsupported_statements_are_denied_even_when_writable(string sql)
    {
        var d = Validate(sql, isReadOnly: false);
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_ddl", d.DenyCode);
    }

    [Fact]
    public void Ddl_without_the_connection_permission_is_denied()
    {
        var d = Validate("DROP TABLE orders");

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_ddl", d.DenyCode);
        Assert.Equal(DdlOperation.DropTable, d.DdlOperation);
        Assert.Contains("DropTable", d.Reason);
    }

    [Fact]
    public void Permitted_ddl_requires_confirmation()
    {
        var d = Validate(
            "DROP TABLE orders",
            allowedDdl: AllowedDdl.DropTable,
            confirmed: false);

        Assert.False(d.Allowed);
        Assert.Equal("ddl_confirmation_required", d.DenyCode);
        Assert.Equal(DdlOperation.DropTable, d.DdlOperation);
    }

    [Fact]
    public void Permitted_and_confirmed_ddl_is_allowed()
    {
        var d = Validate(
            "DROP TABLE orders",
            allowedDdl: AllowedDdl.DropTable,
            confirmed: true);

        Assert.True(d.Allowed);
        Assert.Equal(DdlOperation.DropTable, d.DdlOperation);
    }

    [Fact]
    public void Unsupported_shapes_remain_denied_even_with_every_supported_permission()
    {
        var all = AllowedDdl.CreateTable | AllowedDdl.AlterTable | AllowedDdl.DropTable |
                  AllowedDdl.CreateIndex | AllowedDdl.DropIndex | AllowedDdl.Truncate;
        var d = Validate("GRANT SELECT ON orders TO app", allowedDdl: all, confirmed: true);

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_unsupported", d.DenyCode);
        Assert.Equal(DdlOperation.Unsupported, d.DdlOperation);
    }

    [Fact]
    public void Unconfirmed_dml_is_denied_before_execution()
    {
        var d = Validate("UPDATE orders SET total = 0", confirmed: false);

        Assert.False(d.Allowed);
        Assert.Equal("ddl_confirmation_required", d.DenyCode);
        Assert.Equal(DdlOperation.Unsupported, d.DdlOperation);
    }

    [Fact]
    public void Read_only_connection_denial_wins_over_missing_confirmation()
    {
        var d = Validate("UPDATE orders SET total = 0", isReadOnly: true, confirmed: false);

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_readonly", d.DenyCode);
    }

    // --- Multi-statement batches -----------------------------------------------

    [Fact]
    public void Multi_statement_batch_is_denied()
    {
        var d = Validate("SELECT * FROM orders; SELECT * FROM customers");
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_multi_statement", d.DenyCode);
    }

    [Fact]
    public void Stacked_select_then_delete_is_denied_as_batch()
    {
        // Classic injection shape: a read followed by a hidden write.
        var d = Validate("SELECT 1; DELETE FROM orders", isReadOnly: true);
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_multi_statement", d.DenyCode);
    }

    // --- Table visibility: aliases, joins, CTEs, subqueries --------------------

    [Fact]
    public void Hidden_table_via_alias_is_denied()
    {
        var d = Validate("SELECT s.x FROM secrets s", resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void Hidden_table_in_join_is_denied()
    {
        var d = Validate(
            "SELECT o.id FROM orders o JOIN secrets s ON s.id = o.sid",
            resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void Hidden_table_behind_cte_is_denied()
    {
        // The CTE name `t` is visible-by-default, but the real `secrets` table inside it must be caught.
        var d = Validate(
            "WITH t AS (SELECT * FROM secrets) SELECT * FROM t",
            resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void Cte_named_like_hidden_table_is_allowed_when_only_visible_tables_are_read()
    {
        // Regression (CD-59 review): `secrets` here is a CTE alias, not the hidden base table.
        // The query only reads the visible `orders`, so it must be allowed.
        var d = Validate(
            "WITH secrets AS (SELECT * FROM orders) SELECT * FROM secrets",
            resolve: Policy(hidden: ["secrets"]));
        Assert.True(d.Allowed);
        Assert.DoesNotContain(d.ReferencedTables, t => t.Name == "secrets");
        Assert.Contains(d.ReferencedTables, t => t.Name == "orders");
    }

    [Fact]
    public void Inner_cte_does_not_mask_outer_hidden_table_of_same_name()
    {
        // Regression (CD-59 re-review): the CTE `secrets` is defined only inside the EXISTS subquery,
        // so it must NOT mask the outer real `secrets` reference. The outer hidden table must be denied.
        var d = Validate(
            "SELECT * FROM secrets WHERE EXISTS (WITH secrets AS (SELECT 1) SELECT 1 FROM secrets)",
            resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
        Assert.Contains(d.ReferencedTables, t => t.Name == "secrets");
    }

    [Fact]
    public void Non_recursive_cte_body_referencing_same_name_reads_the_real_table()
    {
        // Regression (CD-59 escalation): a non-recursive CTE cannot see its own name, so the `secrets`
        // inside the body is the real hidden base table and must be denied — not masked by the CTE alias.
        var d = Validate(
            "WITH secrets AS (SELECT * FROM secrets) SELECT * FROM secrets",
            resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void Recursive_cte_self_reference_is_not_a_table()
    {
        // A WITH RECURSIVE CTE may reference itself; that self-reference is the CTE, not a base table.
        var d = Validate(
            "WITH RECURSIVE t AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM t WHERE n < 5) SELECT * FROM t",
            resolve: Policy(hidden: ["t"]));
        Assert.True(d.Allowed);
        Assert.Empty(d.ReferencedTables);
    }

    [Fact]
    public void Cte_does_not_mask_hidden_table_inside_its_body()
    {
        // The CTE alias is dropped, but the hidden `secrets` read inside the body is still caught.
        var d = Validate(
            "WITH v AS (SELECT * FROM secrets) SELECT * FROM v",
            resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void Schema_qualified_table_is_checked_even_when_a_cte_shares_its_name()
    {
        // private.secrets is schema-qualified, so it can never be the CTE — it must still be denied.
        var d = Validate(
            "WITH secrets AS (SELECT 1) SELECT * FROM private.secrets",
            resolve: Policy(hidden: ["private.secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void Hidden_table_in_subquery_is_denied()
    {
        var d = Validate(
            "SELECT * FROM orders WHERE sid IN (SELECT id FROM secrets)",
            resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void Hidden_table_as_insert_target_is_denied()
    {
        // Visitor skips the INSERT target — proves the explicit target extraction works.
        var d = Validate("INSERT INTO secrets (id) VALUES (1)", resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void Hidden_table_as_delete_target_is_denied()
    {
        // Visitor skips DELETE entirely — proves the From/Selection re-visit works.
        var d = Validate("DELETE FROM secrets WHERE id = 1", resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void Schema_qualified_hidden_table_is_denied()
    {
        var d = Validate("SELECT * FROM private.secrets", resolve: Policy(hidden: ["private.secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void Visible_query_with_comments_and_joins_is_allowed()
    {
        var sql = "-- monthly report\nSELECT o.id, c.name FROM orders o JOIN customers c ON c.id = o.cid";
        var d = Validate(sql);
        Assert.True(d.Allowed);
        Assert.Contains(d.ReferencedTables, t => t.Name == "orders");
        Assert.Contains(d.ReferencedTables, t => t.Name == "customers");
    }

    // --- Dialect awareness -----------------------------------------------------

    [Fact]
    public void SqlServer_dialect_parses_bracketed_identifiers_and_top()
    {
        var d = Validate(
            "SELECT TOP 10 * FROM [dbo].[secrets]",
            provider: DatabaseProviderType.SqlServer,
            resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void SqlServer_top_on_visible_table_is_allowed()
    {
        var d = Validate("SELECT TOP 10 * FROM orders", provider: DatabaseProviderType.SqlServer);
        Assert.True(d.Allowed);
        Assert.Contains(d.ReferencedTables, t => t.Name == "orders");
    }

    [Fact]
    public void Postgres_limit_on_visible_table_is_allowed()
    {
        var d = Validate("SELECT * FROM orders LIMIT 10", provider: DatabaseProviderType.Postgres);
        Assert.True(d.Allowed);
        Assert.Contains(d.ReferencedTables, t => t.Name == "orders");
    }

    [Fact]
    public void Postgres_limit_does_not_mask_a_hidden_table()
    {
        var d = Validate("SELECT * FROM secrets LIMIT 10", resolve: Policy(hidden: ["secrets"]));
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    // --- Malformed / empty input ----------------------------------------------

    [Fact]
    public void Unparseable_sql_is_denied()
    {
        var d = Validate("SELCT FROM WHERE");
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_parse_error", d.DenyCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- just a comment")]
    public void Empty_input_is_denied(string sql)
    {
        var d = Validate(sql);
        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_empty", d.DenyCode);
    }

    // --- Per-object access (Phase C1) ------------------------------------------

    [Fact]
    public void A_write_to_a_read_only_object_is_denied()
    {
        var d = Validate("UPDATE orders SET total = 0", resolve: Policy(readOnly: ["orders"]));

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_readonly_object", d.DenyCode);
        Assert.Contains("orders", d.Reason);
    }

    [Fact]
    public void A_read_from_a_read_only_object_is_allowed()
    {
        var d = Validate("SELECT id FROM orders", resolve: Policy(readOnly: ["orders"]));

        Assert.True(d.Allowed);
    }

    [Fact]
    public void Reading_a_read_only_object_inside_a_write_is_allowed()
    {
        // The reason write targets had to be separated at all. A lookup table nobody may modify is still
        // a lookup table: copying out of it into a writable target is an ordinary thing to want.
        var d = Validate(
            "INSERT INTO orders (id) SELECT id FROM staging_orders",
            resolve: Policy(readOnly: ["staging_orders"]));

        Assert.True(d.Allowed);
    }

    [Fact]
    public void A_write_to_a_view_is_denied_as_a_view_write_not_as_a_level()
    {
        // Ordering, asserted directly. A view can only be Not visible or Read-only, so if the level check
        // ran first this code would be unreachable and the message would tell the user to change a level
        // that cannot be changed.
        var d = Validate(
            "UPDATE order_summary SET total = 0",
            resolve: Policy(readOnly: ["order_summary"], views: ["order_summary"]));

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_view_write", d.DenyCode);
    }

    [Fact]
    public void A_read_from_a_view_is_allowed()
    {
        var d = Validate("SELECT id FROM order_summary", resolve: Policy(views: ["order_summary"]));

        Assert.True(d.Allowed);
    }

    [Fact]
    public void A_hidden_object_is_still_denied_as_hidden_even_when_it_is_written_to()
    {
        // Visibility outranks both new checks: a hidden object's very name must not leak, and
        // "you may not write to it" concedes that it exists.
        var d = Validate(
            "UPDATE orders SET total = 0",
            resolve: Policy(hidden: ["orders"], readOnly: ["orders"], views: ["orders"]));

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_hidden_table", d.DenyCode);
    }

    [Fact]
    public void The_connection_read_only_flag_still_outranks_per_object_access()
    {
        var d = Validate("UPDATE orders SET total = 0", isReadOnly: true, resolve: Policy());

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_readonly", d.DenyCode);
    }

    [Fact]
    public void An_object_with_default_policy_is_writable()
    {
        // The phase's central decision, pinned where it is enforced: absence means full access.
        var d = Validate("DELETE FROM orders WHERE id = 1", resolve: Policy());

        Assert.True(d.Allowed);
    }

    [Fact]
    public void An_unqualified_name_takes_the_most_restrictive_level_of_its_matches()
    {
        // Same fail-closed rule the hidden check has always used, now over three levels: a bare name that
        // matches a read-only object in some schema is treated as read-only.
        var resolve = (SqlTableReference t) => t.Schema is null
            ? new ObjectPolicy(ObjectAccess.ReadOnly, false)
            : new ObjectPolicy(ObjectAccess.Full, false);

        var d = Validate("UPDATE orders SET total = 0", resolve: resolve);

        Assert.False(d.Allowed);
        Assert.Equal("policy_denied_readonly_object", d.DenyCode);
    }
}

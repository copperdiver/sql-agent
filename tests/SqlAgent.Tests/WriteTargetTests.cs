using SqlAgent.Core;
using SqlAgent.Core.Policy;

namespace SqlAgent.Tests;

/// <summary>
/// The parser's flat table list cannot say which object a statement writes to, and per-object access
/// needs exactly that. These tests pin the split from the outside — what is written, what is merely read
/// — across both dialects and across the forms that hide the target behind other syntax.
/// </summary>
public class WriteTargetTests
{
    private static ParsedStatement Parse(string sql, DatabaseProviderType provider = DatabaseProviderType.Postgres)
        => Assert.Single(SqlAnalyzer.Analyze(sql, provider));

    private static string[] Names(IEnumerable<SqlTableReference> refs)
        => refs.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void A_select_writes_to_nothing()
    {
        var stmt = Parse("SELECT id FROM orders JOIN customers ON customers.id = orders.customer_id");

        Assert.Empty(stmt.WrittenTables);
        Assert.Equal(["customers", "orders"], Names(stmt.Tables));
    }

    [Fact]
    public void Insert_writes_only_to_its_target()
    {
        var stmt = Parse("INSERT INTO orders (id) VALUES (1)");

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
    }

    [Fact]
    public void Insert_select_writes_to_the_target_and_reads_the_source()
    {
        // The case the flat list could not express, and the reason the Read-only level is worth having:
        // copying out of a lookup table into a writable one must not be refused.
        var stmt = Parse("INSERT INTO orders (id) SELECT id FROM staging_orders");

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Equal(["orders", "staging_orders"], Names(stmt.Tables));
    }

    [Theory]
    [InlineData(DatabaseProviderType.Postgres)]
    [InlineData(DatabaseProviderType.SqlServer)]
    public void Update_from_writes_to_the_target_and_reads_the_from_clause(DatabaseProviderType provider)
    {
        var stmt = Parse(
            "UPDATE orders SET total = s.total FROM staging_orders s WHERE s.id = orders.id", provider);

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Contains("staging_orders", Names(stmt.Tables));
    }

    [Theory]
    [InlineData(DatabaseProviderType.Postgres, "UPDATE o SET total = 0 FROM orders o")]
    [InlineData(DatabaseProviderType.SqlServer, "UPDATE o SET total = 0 FROM orders o")]
    [InlineData(DatabaseProviderType.Postgres, "UPDATE o SET total = 0 FROM orders AS o WHERE o.id = 1")]
    [InlineData(DatabaseProviderType.SqlServer, "UPDATE o SET total = 0 FROM orders AS o WHERE o.id = 1")]
    public void Update_through_an_alias_declared_in_from_writes_to_the_real_table(
        DatabaseProviderType provider, string sql)
    {
        // T-SQL's canonical update-with-join idiom, which both dialects parse: Update.Table holds the
        // *alias*, and the real table is only in Update.From. Recording the alias would hand the policy a
        // name no object has — so it resolves to full access, is not a view, and both the read-only-object
        // and the view-write denials pass on a statement the engine executes against `orders`.
        var stmt = Parse(sql, provider);

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Equal(["orders"], Names(stmt.Tables));
    }

    [Fact]
    public void Update_with_a_subquery_does_not_treat_the_subquery_source_as_written()
    {
        var stmt = Parse("UPDATE orders SET total = (SELECT max(amount) FROM payments)");

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Equal(["orders", "payments"], Names(stmt.Tables));
    }

    [Theory]
    [InlineData(DatabaseProviderType.Postgres)]
    [InlineData(DatabaseProviderType.SqlServer)]
    public void Delete_writes_to_its_target(DatabaseProviderType provider)
    {
        var stmt = Parse("DELETE FROM orders WHERE id = 1", provider);

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
    }

    [Theory]
    [InlineData(DatabaseProviderType.Postgres)]
    [InlineData(DatabaseProviderType.SqlServer)]
    public void Delete_joined_to_a_lookup_reads_the_lookup_rather_than_writing_it(DatabaseProviderType provider)
    {
        // The read side of the split, on the write statement that most needs it: joining a DELETE to a
        // lookup table is ordinary SQL, and marking the whole clause written would let a Read-only lookup
        // refuse it — turning the level into exactly the toxic thing WrittenTables exists to avoid.
        var stmt = Parse("DELETE o FROM orders o JOIN lookup x ON x.id = o.id", provider);

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Equal(["lookup", "orders"], Names(stmt.Tables));
    }

    [Theory]
    [InlineData(DatabaseProviderType.Postgres)]
    [InlineData(DatabaseProviderType.SqlServer)]
    public void Delete_naming_the_joined_relation_writes_that_one_and_not_the_first(DatabaseProviderType provider)
    {
        // The reason the target is resolved by name rather than taken positionally: T-SQL deletes from
        // whichever relation the name ahead of FROM resolves to, and here that is the joined one. Taking
        // the first relation would check `orders` while the engine deletes from `lookup`.
        var stmt = Parse("DELETE x FROM orders o JOIN lookup x ON x.id = o.id", provider);

        Assert.Equal(["lookup"], Names(stmt.WrittenTables));
        Assert.Equal(["lookup", "orders"], Names(stmt.Tables));
    }

    [Fact]
    public void Delete_using_a_second_table_does_not_write_it()
    {
        // Postgres' spelling of the same idiom. USING hangs off its own property, so it was already read
        // only — pinned here so narrowing the FROM clause cannot quietly widen this one.
        var stmt = Parse("DELETE FROM orders o USING lookup x WHERE x.id = o.id");

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Equal(["lookup", "orders"], Names(stmt.Tables));
    }

    [Fact]
    public void Delete_from_several_comma_separated_tables_writes_all_of_them()
    {
        // Neither supported engine accepts this, but the parser does, and every entry of an unqualified
        // DELETE's FROM clause is a target. Narrowing to the first would under-mark.
        var stmt = Parse("DELETE FROM orders, lookup");

        Assert.Equal(["lookup", "orders"], Names(stmt.WrittenTables));
    }

    [Fact]
    public void Delete_with_a_subquery_does_not_treat_the_subquery_source_as_written()
    {
        var stmt = Parse("DELETE FROM orders WHERE id IN (SELECT order_id FROM refunds)");

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Equal(["orders", "refunds"], Names(stmt.Tables));
    }

    [Fact]
    public void A_write_target_named_by_schema_keeps_its_schema()
    {
        var stmt = Parse("UPDATE sales.orders SET total = 0");

        var target = Assert.Single(stmt.WrittenTables);
        Assert.Equal("sales", target.Schema);
        Assert.Equal("orders", target.Name);
    }

    [Fact]
    public void A_cte_alias_is_not_a_write_target()
    {
        // The CTE name is not an object, so it must reach neither list — the same rule the visibility
        // check has always applied, now also on the write side.
        var stmt = Parse("WITH recent AS (SELECT id FROM orders) INSERT INTO archive SELECT id FROM recent");

        Assert.Equal(["archive"], Names(stmt.WrittenTables));
        Assert.DoesNotContain("recent", Names(stmt.Tables));
    }

    [Fact]
    public void A_write_target_shadowed_by_a_cte_of_the_same_name_is_still_the_real_table()
    {
        // WITH orders AS (...) INSERT INTO orders ... looks like a self-referencing CTE, but neither
        // engine lets you write into a WITH alias: the INSERT target resolves against the catalog, so
        // this writes the real "orders" table. The CTE short-circuit that drops a shadowed read source
        // must not also drop this — it is the one reference the write actually touches.
        var stmt = Parse("WITH orders AS (SELECT 1) INSERT INTO orders SELECT 1");

        Assert.Equal(["orders"], Names(stmt.WrittenTables));
        Assert.Equal(SqlStatementKind.Write, stmt.Kind);
    }

    [Fact]
    public void A_cte_wrapped_insert_classifies_as_write_not_read()
    {
        // WITH cte AS (...) INSERT INTO t SELECT ... parses as a top-level Statement.Select — the INSERT
        // lives nested inside the query body — so the syntactic-node switch alone would call this a Read
        // and let it skip the read-only connection gate entirely. The write set must override that.
        var stmt = Parse("WITH recent AS (SELECT id FROM orders) INSERT INTO archive SELECT id FROM recent");

        Assert.Equal(SqlStatementKind.Write, stmt.Kind);
    }

    [Fact]
    public void A_plain_select_stays_read_with_no_write_set()
    {
        // The upgrade is one-directional: finding a write nested somewhere pushes Read to Write, but the
        // ordinary case (nothing written) must not be disturbed by the same code path.
        var stmt = Parse("WITH recent AS (SELECT id FROM orders) SELECT id FROM recent");

        Assert.Equal(SqlStatementKind.Read, stmt.Kind);
        Assert.Empty(stmt.WrittenTables);
    }

    [Fact]
    public void A_write_whose_target_cannot_be_identified_treats_every_reference_as_written()
    {
        // The fail-closed fallback, asserted through the invariant rather than by breaking the parser:
        // a Write statement must never report an empty write set, because an empty one would mean the
        // per-object check silently passes on a statement that does modify something.
        foreach (var sql in new[]
        {
            "INSERT INTO orders (id) VALUES (1)",
            "UPDATE orders SET total = 0",
            "DELETE FROM orders",
        })
        {
            var stmt = Parse(sql);
            Assert.Equal(SqlStatementKind.Write, stmt.Kind);
            Assert.NotEmpty(stmt.WrittenTables);
        }
    }

    [Fact]
    public void Every_written_table_is_also_a_referenced_table()
    {
        // Tables keeps its old meaning, so the visibility check does not change behaviour: a write target
        // is still checked for visibility exactly as before.
        var stmt = Parse("UPDATE orders SET total = s.total FROM staging_orders s");

        foreach (var written in stmt.WrittenTables)
            Assert.Contains(written, stmt.Tables);
    }
}

using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SqlAgent.Api.Mcp;
using SqlAgent.Core;
using SqlAgent.Providers.Postgres;
using SqlAgent.Providers.SqlServer;
using SqlAgent.Storage;

var builder = Host.CreateApplicationBuilder(args);

// MCP speaks JSON-RPC over stdout; anything else on stdout corrupts the stream. Send all logs to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var dbPath = Environment.GetEnvironmentVariable("SQLAGENT_DB") ?? "sqlagent.db";
builder.Services.AddDbContext<SqlAgentDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton<IDatabaseProvider, PostgresProvider>();
builder.Services.AddSingleton<IDatabaseProvider, SqlServerProvider>();
builder.Services.AddSingleton<IDatabaseProviderRegistry, DatabaseProviderRegistry>();
#pragma warning disable CA1416 // ponytail: v1 secret store is Windows DPAPI; swap in a non-DPAPI ISecretStore when the linux daemon lands.
builder.Services.AddScoped<ISecretStore, DpapiSecretStore>();
#pragma warning restore CA1416
builder.Services.AddScoped<DatabaseConnectionService>();
builder.Services.AddScoped<SchemaService>();
builder.Services.AddScoped<QueryExecutionService>();
builder.Services.AddScoped<LocalTokenAuthenticator>();
builder.Services.AddScoped<McpToolService>();

// Local-access token gate (CD-51 Story 1.7). The client presents its token via the SQLAGENT_AUTH_TOKEN env
// var when it launches this stdio server; the expected token is configured under SqlAgent:LocalAuth:Token.
builder.Services.AddSingleton(new McpClientToken(Environment.GetEnvironmentVariable("SQLAGENT_AUTH_TOKEN")));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// First-run convenience: the SQLite config store shares the daemon's DB file (CD-50 ADR-0004).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<SqlAgentDbContext>().Database.EnsureCreated();
    await scope.ServiceProvider.GetRequiredService<LocalTokenAuthenticator>()
        .ConfigureFromSettingAsync(builder.Configuration["SqlAgent:LocalAuth:Token"]);
}

await app.RunAsync();

namespace SqlAgent.Api.Mcp
{
    /// <summary>
    /// The CD-50 T7 MCP surface: thin wrappers that the SDK exposes as the <c>list_databases</c>,
    /// <c>describe_schema</c>, and <c>query_database</c> tools. All logic and error handling live in
    /// <see cref="McpToolService"/>, which the SDK resolves per call from the request's DI scope.
    /// </summary>
    [McpServerToolType]
    public static class DatabaseTools
    {
        [McpServerTool(Name = "list_databases")]
        [Description("List the configured database connections the agent can access, with their provider and read-only flag.")]
        public static Task<ListDatabasesResponse> ListDatabases(McpToolService tools, CancellationToken ct)
            => tools.ListDatabasesAsync(ct);

        [McpServerTool(Name = "describe_schema")]
        [Description("Return the policy-filtered schema (tables, columns, primary keys, foreign keys) for a database. Tables hidden by policy are omitted.")]
        public static Task<DescribeSchemaResponse> DescribeSchema(
            McpToolService tools,
            [Description("The database connection id, as returned by list_databases.")] string database_id,
            CancellationToken ct)
            => tools.DescribeSchemaAsync(database_id, ct);

        [McpServerTool(Name = "query_database")]
        [Description("Run a SQL query against a database. The query is policy-validated before execution: writes on read-only connections and access to hidden tables are denied. Results are row-capped.")]
        public static Task<QueryDatabaseResponse> QueryDatabase(
            McpToolService tools,
            [Description("The database connection id, as returned by list_databases.")] string database_id,
            [Description("The SQL query to execute.")] string sql,
            CancellationToken ct)
            => tools.QueryDatabaseAsync(database_id, sql, ct);
    }
}

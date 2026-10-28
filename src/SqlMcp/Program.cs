using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlMcp;
using SqlMcp.Auditing;
using SqlMcp.Configuration;
using SqlMcp.Data;
using SqlMcp.Safety;

var builder = Host.CreateApplicationBuilder(args);

// "--config path/to/sql-mcp.json"; env vars and arguments are re-added after it so they still win.
if (builder.Configuration["config"] is { Length: > 0 } configFile)
{
    builder.Configuration.AddJsonFile(Path.GetFullPath(configFile), optional: false, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddCommandLine(args);
}

// stdout carries the MCP protocol, so every log line has to go to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddOptions<SqlMcpOptions>()
    .Bind(builder.Configuration.GetSection(SqlMcpOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<AccessPolicy>();
builder.Services.AddSingleton<QueryValidator>();
builder.Services.AddSingleton<QueryExecutor>();
builder.Services.AddSingleton<PlanExplainer>();
builder.Services.AddSingleton<SchemaCatalog>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddHostedService<StartupCheck>();

builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "sql-mcp", Version = ThisAssembly.Version })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlMcp.Data;

namespace SqlMcp;

internal static class ThisAssembly
{
    public static string Version { get; } =
        typeof(ThisAssembly).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}

/// <summary>Warns on stderr at startup when the configured login can write, since the database's permissions are the real security boundary.</summary>
internal sealed partial class StartupCheck(SchemaCatalog catalog, ILogger<StartupCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var info = await catalog.GetServerInfoAsync(cancellationToken);
            LogConnected(info.Database, info.Login, info.Version);

            foreach (var warning in info.PrivilegeWarnings)
            {
                LogPrivilegeWarning(warning);
            }
        }
        catch (SqlException ex)
        {
            // Starting anyway lets the agent see the connection error through server_info instead of a dead process.
            LogConnectionFailed(ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {Database} as {Login} (SQL Server {Version}).")]
    private partial void LogConnected(string database, string login, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Warning} sql-mcp blocks writes itself, but a least-privilege login is the real boundary; see scripts/create-readonly-user.sql.")]
    private partial void LogPrivilegeWarning(string warning);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not connect to SQL Server at startup.")]
    private partial void LogConnectionFailed(Exception exception);
}

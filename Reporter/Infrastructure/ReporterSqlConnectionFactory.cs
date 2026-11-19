using DocsApi.Reporter.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Infrastructure;

public interface IReporterSqlConnectionFactory
{
    Task<SqlConnection> OpenAppConnectionAsync(CancellationToken ct);
    Task<SqlConnection> OpenSourceConnectionAsync(string sourceCode, CancellationToken ct);
}

public sealed class ReporterSqlConnectionFactory : IReporterSqlConnectionFactory
{
    private readonly ReporterOptions _options;

    public ReporterSqlConnectionFactory(IOptions<ReporterOptions> options)
    {
        _options = options.Value;
    }

    public async Task<SqlConnection> OpenAppConnectionAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.AppConnectionString))
            throw new InvalidOperationException("Reporter:AppConnectionString is empty.");

        var connection = new SqlConnection(_options.AppConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var guard = connection.CreateCommand();
            guard.CommandTimeout = 10;
            guard.CommandText = @"SELECT CASE WHEN DB_NAME() IN (N'master',N'model',N'msdb',N'tempdb')
 OR OBJECT_ID(N'dbo.ParameterGroups',N'U') IS NOT NULL
 OR OBJECT_ID(N'app.Source',N'U') IS NULL THEN 0 ELSE 1 END;";
            if (Convert.ToInt32(await guard.ExecuteScalarAsync(ct)) != 1)
                throw new InvalidOperationException("AppConnectionString должен вести в отдельную подготовленную БД репортера.");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<SqlConnection> OpenSourceConnectionAsync(string sourceCode, CancellationToken ct)
    {
        if (!_options.SourceConnectionStrings.TryGetValue(sourceCode, out var connectionString) ||
            string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string for source '{sourceCode}' not found in Reporter:SourceConnectionStrings.");
        }

        var connection = new SqlConnection(connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}

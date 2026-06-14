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
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task<SqlConnection> OpenSourceConnectionAsync(string sourceCode, CancellationToken ct)
    {
        if (!_options.SourceConnectionStrings.TryGetValue(sourceCode, out var connectionString) ||
            string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string for source '{sourceCode}' not found in Reporter:SourceConnectionStrings.");
        }

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

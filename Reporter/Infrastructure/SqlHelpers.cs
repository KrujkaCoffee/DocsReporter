using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;

namespace DocsApi.Reporter.Infrastructure;

public static class SqlHelpers
{
    public static string QuoteName(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("SQL identifier is empty.", nameof(identifier));

        if (identifier.Length > 128)
            throw new ArgumentException("SQL identifier is too long.", nameof(identifier));

        return "[" + identifier.Replace("]", "]]") + "]";
    }

    public static async Task<bool> TableExistsAsync(SqlConnection connection, string schema, string table, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(@fullName, N'U') IS NULL THEN 0 ELSE 1 END";
        cmd.Parameters.Add(new SqlParameter("@fullName", SqlDbType.NVarChar, 300) { Value = schema + "." + table });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1;
    }

    public static async Task<HashSet<string>> GetColumnsAsync(SqlConnection connection, string schema, string table, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SELECT c.name
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @schema AND o.name = @table AND o.type = 'U';";
        cmd.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = schema });
        cmd.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 128) { Value = table });

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(reader.GetString(0));

        return result;
    }

    public static string? FirstExisting(HashSet<string> columns, params string[] candidates)
    {
        foreach (var c in candidates)
            if (columns.Contains(c)) return c;
        return null;
    }

    public static string? GetNullableString(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public static long GetInt64Flexible(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    public static int GetInt32Flexible(this SqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }
}

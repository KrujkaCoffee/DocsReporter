using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Security.Claims;
using DocsApi.Reporter.Dto;
using DocsApi.Reporter.Infrastructure;
using DocsApi.Reporter.Options;
using Microsoft.Extensions.Options;

namespace DocsApi.Reporter.Services;

public interface IFederatedProjectCardSearchService
{
    Task<FederatedProjectCardSearchDto> SearchAsync(
        ClaimsPrincipal user,
        string query,
        IReadOnlyCollection<string>? requestedSources,
        int page,
        int pageSize,
        int? sourceTimeoutSeconds,
        CancellationToken ct);
}

public sealed class FederatedProjectCardSearchService : IFederatedProjectCardSearchService
{
    private readonly IReporterSqlConnectionFactory _factory;
    private readonly IProjectCardExplorerService _projectCards;
    private readonly ReporterOptions _options;

    public FederatedProjectCardSearchService(
        IReporterSqlConnectionFactory factory,
        IProjectCardExplorerService projectCards,
        IOptions<ReporterOptions> options)
    {
        _factory = factory;
        _projectCards = projectCards;
        _options = options.Value;
    }

    public async Task<FederatedProjectCardSearchDto> SearchAsync(
        ClaimsPrincipal user,
        string query,
        IReadOnlyCollection<string>? requestedSources,
        int page,
        int pageSize,
        int? sourceTimeoutSeconds,
        CancellationToken ct)
    {
        query = (query ?? string.Empty).Trim();
        if (query.Length == 0)
            throw new ArgumentException("Query is required.", nameof(query));

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, Math.Clamp(_options.FederatedMaxPageSize, 1, 500));

        var maxSources = Math.Clamp(_options.FederatedMaxSources, 1, 50);
        var maxConcurrency = Math.Clamp(_options.FederatedMaxConcurrency, 1, maxSources);
        var timeoutSeconds = Math.Clamp(
            sourceTimeoutSeconds ?? _options.FederatedSourceTimeoutSeconds,
            2,
            120);

        var sourceCatalog = await LoadSourceCatalogAsync(ct);
        var selectedSources = ResolveSources(sourceCatalog, requestedSources, maxSources);
        if (selectedSources.Count == 0)
            throw new InvalidOperationException("No reporter sources are configured or enabled.");

        var totalStopwatch = Stopwatch.StartNew();
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var resultByCode = new ConcurrentDictionary<string, FederatedSourceSearchResultDto>(StringComparer.OrdinalIgnoreCase);

        var tasks = selectedSources.Select(async source =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var result = await SearchSourceAsync(
                    user,
                    source,
                    query,
                    page,
                    pageSize,
                    timeoutSeconds,
                    ct);
                resultByCode[source.Code] = result;
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        totalStopwatch.Stop();

        var sourceOrder = selectedSources
            .Select((source, index) => new { source.Code, Index = index })
            .ToDictionary(x => x.Code, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var sourceResults = selectedSources
            .Select(source => resultByCode[source.Code])
            .ToArray();

        var allItems = sourceResults
            .Where(result => string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase))
            .SelectMany(result => result.Items)
            .OrderBy(item => GetMatchRank(item, query))
            .ThenBy(item => item.ObjectCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => sourceOrder.TryGetValue(item.SourceCode, out var sourceIndex) ? sourceIndex : int.MaxValue)
            .ThenByDescending(item => item.ObjectId)
            .ToArray();

        var groups = allItems
            .GroupBy(GetComparisonKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group
                    .OrderBy(item => sourceOrder.TryGetValue(item.SourceCode, out var sourceIndex) ? sourceIndex : int.MaxValue)
                    .ThenByDescending(item => item.ObjectId)
                    .ToArray();
                var representative = items
                    .OrderBy(item => GetMatchRank(item, query))
                    .First();
                return new FederatedProjectCardGroupDto(
                    group.Key,
                    representative.ObjectCode,
                    representative.Name,
                    items.Select(item => item.SourceCode).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    items);
            })
            .OrderBy(group => GetGroupRank(group, query))
            .ThenBy(group => group.ObjectCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var successfulCount = sourceResults.Count(result => string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase));
        var failedCount = sourceResults.Length - successfulCount;

        return new FederatedProjectCardSearchDto(
            query,
            page,
            pageSize,
            selectedSources.Select(source => source.Code).ToArray(),
            allItems.Length,
            successfulCount,
            failedCount,
            failedCount > 0,
            totalStopwatch.ElapsedMilliseconds,
            sourceResults,
            groups);
    }

    private async Task<FederatedSourceSearchResultDto> SearchSourceAsync(
        ClaimsPrincipal user,
        SourceDefinition source,
        string query,
        int page,
        int pageSize,
        int timeoutSeconds,
        CancellationToken requestToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var items = await _projectCards.SearchAsync(
                user,
                source.Code,
                query,
                page,
                pageSize,
                timeoutCts.Token);

            stopwatch.Stop();
            var ordered = items
                .OrderBy(item => GetMatchRank(item, query))
                .ThenBy(item => item.ObjectCode, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(item => item.ObjectId)
                .ToArray();

            return new FederatedSourceSearchResultDto(
                source.Code,
                source.DisplayName,
                "ok",
                stopwatch.ElapsedMilliseconds,
                ordered.Length,
                null,
                ordered);
        }
        catch (OperationCanceledException) when (!requestToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new FederatedSourceSearchResultDto(
                source.Code,
                source.DisplayName,
                "timeout",
                stopwatch.ElapsedMilliseconds,
                0,
                $"Source did not respond within {timeoutSeconds} seconds.",
                Array.Empty<ProjectCardSearchItemDto>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            stopwatch.Stop();
            return new FederatedSourceSearchResultDto(
                source.Code,
                source.DisplayName,
                "forbidden",
                stopwatch.ElapsedMilliseconds,
                0,
                SafeError(ex),
                Array.Empty<ProjectCardSearchItemDto>());
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new FederatedSourceSearchResultDto(
                source.Code,
                source.DisplayName,
                "error",
                stopwatch.ElapsedMilliseconds,
                0,
                SafeError(ex),
                Array.Empty<ProjectCardSearchItemDto>());
        }
    }

    private async Task<IReadOnlyList<SourceDefinition>> LoadSourceCatalogAsync(CancellationToken ct)
    {
        var sources = new List<SourceDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appCatalogLoaded = false;

        try
        {
            await using var app = await _factory.OpenAppConnectionAsync(ct);
            await using var cmd = app.CreateCommand();
            cmd.CommandType = CommandType.Text;
            cmd.CommandText = @"
SELECT Code, DisplayName
FROM app.Source
WHERE IsEnabled = 1
ORDER BY DisplayName, Code;";

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            appCatalogLoaded = true;
            while (await reader.ReadAsync(ct))
            {
                var code = reader.GetString(0).Trim();
                if (code.Length == 0 || !seen.Add(code))
                    continue;

                sources.Add(new SourceDefinition(
                    code,
                    reader.IsDBNull(1) ? code : reader.GetString(1)));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The source connection dictionary remains a safe fallback for UI/dev mode.
        }

        if (!appCatalogLoaded)
        {
            foreach (var code in _options.SourceConnectionStrings.Keys.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(code) || !seen.Add(code))
                    continue;
                sources.Add(new SourceDefinition(code, code));
            }
        }

        return sources;
    }

    private static IReadOnlyList<SourceDefinition> ResolveSources(
        IReadOnlyList<SourceDefinition> catalog,
        IReadOnlyCollection<string>? requestedSources,
        int maxSources)
    {
        var byCode = catalog.ToDictionary(source => source.Code, StringComparer.OrdinalIgnoreCase);
        var requested = requestedSources?
            .Select(source => source?.Trim())
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxSources)
            .ToArray();

        if (requested is null || requested.Length == 0)
            return catalog.Take(maxSources).ToArray();

        return requested
            .Select(code => byCode.TryGetValue(code, out var source)
                ? source
                : new SourceDefinition(code, code))
            .ToArray();
    }

    private static string GetComparisonKey(ProjectCardSearchItemDto item)
    {
        if (!string.IsNullOrWhiteSpace(item.ObjectCode))
        {
            var normalized = string.Concat(item.ObjectCode
                .Trim()
                .Where(character => !char.IsWhiteSpace(character)))
                .ToUpperInvariant();
            if (normalized.Length > 0)
                return $"code:{normalized}";
        }

        return $"object:{item.SourceCode}:{item.ObjectId}";
    }

    private static int GetGroupRank(FederatedProjectCardGroupDto group, string query)
    {
        var item = group.Items.FirstOrDefault();
        return item is null ? int.MaxValue : GetMatchRank(item, query);
    }

    private static int GetMatchRank(ProjectCardSearchItemDto item, string query)
    {
        var code = item.ObjectCode?.Trim() ?? string.Empty;
        var name = item.Name?.Trim() ?? string.Empty;

        if (code.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (code.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (code.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 4;
        return 5;
    }

    private static string SafeError(Exception ex)
    {
        var message = ex.Message
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return message.Length <= 400 ? message : message[..400] + "…";
    }

    private sealed record SourceDefinition(string Code, string DisplayName);
}

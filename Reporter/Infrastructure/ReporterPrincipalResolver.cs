using System.Security.Claims;
using DocsApi.Reporter.Options;

namespace DocsApi.Reporter.Infrastructure;

public sealed record ReporterPrincipalSnapshot(
    string Login,
    string? Sid,
    bool IsAuthenticated,
    string? AuthenticationType,
    bool IsDebugIdentity,
    IReadOnlyList<string> CandidateLogins);

public static class ReporterPrincipalResolver
{
    private const string PrimarySidClaimType =
        "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid";

    public static ReporterPrincipalSnapshot Resolve(
        ClaimsPrincipal user,
        ReporterOptions options)
    {
        var authenticated = user.Identity?.IsAuthenticated == true;
        var login = user.Identity?.Name?.Trim();
        var sid = user.FindFirst(ClaimTypes.PrimarySid)?.Value
            ?? user.FindFirst(PrimarySidClaimType)?.Value;
        var authenticationType = user.Identity?.AuthenticationType;
        var isDebug = false;

        var debugAllowed = options.SecurityMode.Equals(
            "Preview",
            StringComparison.OrdinalIgnoreCase);

        if ((!authenticated || string.IsNullOrWhiteSpace(login))
            && debugAllowed
            && !string.IsNullOrWhiteSpace(options.DebugWindowsLogin))
        {
            login = options.DebugWindowsLogin.Trim();
            sid = string.IsNullOrWhiteSpace(options.DebugWindowsSid)
                ? sid
                : options.DebugWindowsSid.Trim();
            authenticationType = "ReporterDebugIdentity";
            isDebug = true;
        }

        login = string.IsNullOrWhiteSpace(login) ? "anonymous" : login;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!login.Equals("anonymous", StringComparison.OrdinalIgnoreCase))
        {
            Add(candidates, login);

            var slash = login.LastIndexOf('\\');
            if (slash >= 0 && slash + 1 < login.Length)
                Add(candidates, login[(slash + 1)..]);

            var at = login.IndexOf('@');
            if (at > 0)
                Add(candidates, login[..at]);
        }

        return new ReporterPrincipalSnapshot(
            login,
            string.IsNullOrWhiteSpace(sid) ? null : sid.Trim(),
            authenticated,
            authenticationType,
            isDebug,
            candidates.ToArray());
    }

    private static void Add(HashSet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(value.Trim());
    }
}

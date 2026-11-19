using System.Security.Claims;
using DocsApi.Reporter.Options;
using DocsApi.Reporter.StrictAccess;

namespace DocsApi.Reporter.Infrastructure;

public sealed record ReporterPrincipalSnapshot(string Login, string? Sid, bool IsAuthenticated,
    string? AuthenticationType, bool IsDebugIdentity, IReadOnlyList<string> CandidateLogins);

public static class ReporterPrincipalResolver
{
    public static ReporterPrincipalSnapshot Resolve(ClaimsPrincipal user, ReporterOptions options)
    {
        var actor = RuntimePrincipal.Read(user) ?? throw new UnauthorizedAccessException("Требуется подтверждённая учётная запись Windows.");
        // No short-login or alias expansion here. Legacy identity service is not on the public route list.
        return new(actor.Login, actor.Sid, true, user.Identity?.AuthenticationType, false, new[] { actor.Login });
    }
}

using System.Security.Claims;
using System.Security.Principal;

namespace DocsApi.Reporter.StrictAccess;

public sealed record RuntimeUser(string Login, string Sid, string Domain, string Account);
public static class RuntimePrincipal
{
    public static RuntimeUser? Read(ClaimsPrincipal principal)
    {
        var id = principal.Identity;
        if (id?.IsAuthenticated != true || string.IsNullOrWhiteSpace(id.Name)) return null;
        var type = id.AuthenticationType ?? "";
        if (id is not WindowsIdentity && !new[] { "Negotiate", "Kerberos", "NTLM" }.Contains(type, StringComparer.OrdinalIgnoreCase))
            return null;
        var sid = principal.FindFirst(ClaimTypes.PrimarySid)?.Value;
        if (OperatingSystem.IsWindows() && id is WindowsIdentity wi) sid = wi.User?.Value ?? sid;
        if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-", StringComparison.Ordinal) ||
            sid.Length > 184 || sid.Any(c => !char.IsAsciiDigit(c) && c != 'S' && c != '-')) return null;
        var login = id.Name.Trim();
        if (login.Length > 255) return null;
        var parts = login.Split('\\');
        // SID matching works even with a UPN. Bare-account fallback never works without a trusted domain.
        return new(login, sid, parts.Length == 2 ? parts[0] : "", parts.Length == 2 ? parts[1] : "");
    }
    public static bool IsAdministrator(RuntimeUser user, StrictRuntimeOptions options) =>
        options.AdministratorSids.Contains(user.Sid, StringComparer.OrdinalIgnoreCase);
}

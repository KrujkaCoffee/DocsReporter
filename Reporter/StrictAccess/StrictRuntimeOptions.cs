namespace DocsApi.Reporter.StrictAccess;

public sealed class StrictRuntimeOptions
{
    // No switch to turn strict access into Preview or anonymous mode.
    public int MaxAclRows { get; set; } = 10000;
    public int MaxPrincipalNodes { get; set; } = 1024;
    public int MaxHierarchyDepth { get; set; } = 32;
    public int SqlTimeoutSeconds { get; set; } = 15;
    public int AuditTimeoutSeconds { get; set; } = 5;
    // Source code -> ParameterGroups.Guid -> verified object-read command ID.
    // Empty by default. Populating this is a semantic verification, not a user permission grant.
    public Dictionary<string, Dictionary<string, int>> VerifiedObjectReadCommands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ProvisionNewUsers { get; set; } = true;
    public bool AllowDomainLoginFallback { get; set; }
    public string[] TrustedLoginDomains { get; set; } = Array.Empty<string>();
    public string[] AdministratorSids { get; set; } = Array.Empty<string>();
    // A restriction only. No administrative SID bypasses resource ACL.
    public string[] BlockedPrincipalGuids { get; set; } = new[]
        { "1F387E88-75EA-443A-9CD9-331C23FD9B0C" };
}

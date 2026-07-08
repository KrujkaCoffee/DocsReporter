namespace DocsApi.Reporter.Options;

public sealed class ReporterOptions
{
    public string AppConnectionString { get; set; } = string.Empty;
    public string DefaultRoleCode { get; set; } = "ProjectCardViewer";
    public Dictionary<string, string> SourceConnectionStrings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Federated search defaults. They can be overridden in appsettings.json under Reporter.
    public int FederatedMaxSources { get; set; } = 10;
    public int FederatedMaxConcurrency { get; set; } = 4;
    public int FederatedSourceTimeoutSeconds { get; set; } = 15;
    public int FederatedMaxPageSize { get; set; } = 100;

    // Security modes:
    // Disabled  - security diagnostics are disabled, reporter endpoints stay permissive.
    // Preview   - identity and xAccessRights are visible in diagnostics, but are not enforced.
    // AppPolicy - app.GroupAccessPolicy/app.RelationAccessPolicy are enforced.
    // TflexRead - app policies plus source-scoped T-FLEX read rights are enforced.
    public string SecurityMode { get; set; } = "Preview";
    public string? DebugWindowsLogin { get; set; }
    public string? DebugWindowsSid { get; set; }
    public int SecurityMaxHierarchyDepth { get; set; } = 16;
    public int SecurityMaxRightsRows { get; set; } = 500;

    // Stage 5B: T-FLEX read enforcement is intentionally conservative.
    // CommandID=1 is treated as the read/open command when AccessGroupCommands is available.
    public int TflexReadCommandId { get; set; } = 1;
    public bool TflexDenyWhenNoReadRule { get; set; } = true;
    public int TflexFilesReferenceId { get; set; } = 16;
    public bool TflexReadFilterFiles { get; set; } = true;

    // Stage 6: lightweight reporter admin UI. In Preview mode it can be opened without Windows Auth
    // to keep manual testing simple. In AppPolicy/TflexRead modes admin endpoints require ReportAdmin or Developer role.
    public bool AdminAllowAnonymousInPreview { get; set; } = true;
    public int AdminDefaultTake { get; set; } = 200;
}

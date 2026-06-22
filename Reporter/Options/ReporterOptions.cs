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

    // Stage 5A: identity/access preview. Preview does not enforce T-FLEX ACL yet.
    public string SecurityMode { get; set; } = "Preview";
    public string? DebugWindowsLogin { get; set; }
    public string? DebugWindowsSid { get; set; }
    public int SecurityMaxHierarchyDepth { get; set; } = 16;
    public int SecurityMaxRightsRows { get; set; } = 500;
}

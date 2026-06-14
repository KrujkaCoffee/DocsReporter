namespace DocsApi.Reporter.Options;

public sealed class ReporterOptions
{
    public string AppConnectionString { get; set; } = string.Empty;
    public string DefaultRoleCode { get; set; } = "ProjectCardViewer";
    public Dictionary<string, string> SourceConnectionStrings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

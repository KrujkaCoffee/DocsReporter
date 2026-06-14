namespace DocsApi.Configurations
{
    public class FileSettings
    {
        public Dictionary<string, string> SrvPaths { get; set; }
    };
    public class ProcessTkpRefs
    {
        public string srvTdocs { get; set; }
        public string srvDocs { get; set; }
        public List<string> directories { get; set; }
    };

}

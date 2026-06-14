namespace DocsApi.Models
{
    public class CodeErpDbResponse
    {
        public int s_ObjectID { get; set; }
        public string? Name { get; set; }
        public string? Kod_ERP { get; set; }
    }

    public class FileObjectId
    {
        public int s_ObjectID { get; set; }
        public int s_Version { get; set; }
        public string? Name { get; set; }
        public int NomenId { get; set; }
    }
    public class FileObjectIdWithSource
    {
        public int s_ObjectID { get; set; }
        public int s_Version { get; set; }
        public string? Name { get; set; }
        public int NomenId { get; set; }
        public string? Source { get; set; }
    }

}

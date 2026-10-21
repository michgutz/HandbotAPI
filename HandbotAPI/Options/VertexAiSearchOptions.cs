namespace HandbotAPI.Options
{
    public class VertexAiSearchOptions
    {
        public const string SectionName = "VertexAiSearch";

        public string ProjectId { get; set; } = string.Empty;
        public string Location { get; set; } = "global";
        public string DataStoreId { get; set; } = string.Empty;
    }
}

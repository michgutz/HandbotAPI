namespace HandbotAPI.Options
{
    public class ClaudeOptions
    {
        public const string SectionName = "Claude";

        public string ProjectId { get; set; } = string.Empty;
        public string Region { get; set; } = "global";
        public string ApiKeySecret { get; set; } = string.Empty;
        public string Model { get; set; } = "claude-opus-5-5";
        public int MaxTokens { get; set; } = 16000;
    }
}

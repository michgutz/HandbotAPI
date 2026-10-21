namespace HandbotAPI.Options
{
    public class AnthropicOptions
    {
        public const string SectionName = "Anthropic";

        public string ProjectId { get; set; } = string.Empty;
        public string ApiKeySecretId { get; set; } = string.Empty;
        public string ApiKeySecretVersion { get; set; } = "latest";
        public string Model { get; set; } = "claude-sonnet-4-5";
        public int MaxTokens { get; set; } = 1024;
    }
}

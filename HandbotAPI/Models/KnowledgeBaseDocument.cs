namespace HandbotAPI.Models
{
    /// <summary>A document in the knowledge base index, joined with its PDF in Cloud Storage.</summary>
    /// <param name="FileName">Name of the stored PDF; null when the PDF is not in the configured bucket folder.</param>
    /// <param name="SourceUrl">Original document (Drive, Sites...) that the bot cites.</param>
    /// <param name="IndexedAt">Null while the document is still being indexed.</param>
    public record KnowledgeBaseDocument(
        string Id,
        string Title,
        string? FileName,
        long? Size,
        string? SourceUrl,
        string? SourceType,
        DateTimeOffset? UploadedAt,
        DateTimeOffset? IndexedAt);

    /// <summary>What <c>POST /Documents</c> returns once the PDF is stored and its import has started.</summary>
    /// <param name="Operation">Discovery Engine long-running operation that is indexing the document.</param>
    public record UploadedDocument(
        string Id,
        string Title,
        string FileName,
        long Size,
        DateTimeOffset CreatedAt,
        string? SourceUrl,
        string Operation,
        string Status);

    public class UploadDocumentRequest
    {
        public IFormFile? File { get; set; }

        /// <summary>Optional; defaults to a slug of the title.</summary>
        public string? Id { get; set; }

        /// <summary>Optional; defaults to the file name without its extension.</summary>
        public string? Title { get; set; }

        public string? SourceUrl { get; set; }
    }
}

namespace HandbotAPI.Options
{
    public class DocumentStorageOptions
    {
        public const string SectionName = "DocumentStorage";

        /// <summary>Cloud Storage bucket that holds the PDFs the knowledge base indexes.</summary>
        public string Bucket { get; set; } = string.Empty;

        /// <summary>Folder inside the bucket; each document is stored as {Prefix}/{id}.pdf.</summary>
        public string Prefix { get; set; } = "handbooks";

        /// <summary>Keep in sync with MAX_UPLOAD_BYTES in the dashboard.</summary>
        public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;
    }
}

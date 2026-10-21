using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;
using HandbotAPI.Models;
using HandbotAPI.Options;
using Microsoft.Extensions.Options;

namespace HandbotAPI.Services
{
    public interface IDocumentService
    {
        Task<IReadOnlyList<KnowledgeBaseDocument>> ListAsync(CancellationToken cancellationToken = default);
        Task<UploadedDocument> UploadAsync(DocumentUpload upload, CancellationToken cancellationToken = default);
    }

    public record DocumentUpload(string Id, string Title, string FileName, string? SourceUrl, Stream Content);

    public class DocumentAlreadyExistsException : Exception
    {
        public DocumentAlreadyExistsException(string id)
            : base($"A document with id '{id}' already exists. Send a different id or title.")
        {
        }
    }

    /// <summary>
    /// The PDFs behind the knowledge base: each one is stored in Cloud Storage as {Prefix}/{id}.pdf
    /// and imported into the Vertex AI Search data store under the same id.
    /// </summary>
    public class DocumentService : IDocumentService
    {
        private const string Scope = "https://www.googleapis.com/auth/cloud-platform";
        private const string PdfMimeType = "application/pdf";
        private const string StorageApi = "https://storage.googleapis.com/storage/v1";
        private const string StorageUploadApi = "https://storage.googleapis.com/upload/storage/v1";
        // Same metadata as the documents loaded at setup, so uploads can be told apart by source_type.
        private const string Handbook = "Global Handbook";
        private const string UploadSourceType = "upload";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _httpClient;
        private readonly VertexAiSearchOptions _searchOptions;
        private readonly DocumentStorageOptions _storageOptions;
        private readonly ILogger<DocumentService> _logger;

        public DocumentService(
            HttpClient httpClient,
            IOptions<VertexAiSearchOptions> searchOptions,
            IOptions<DocumentStorageOptions> storageOptions,
            ILogger<DocumentService> logger)
        {
            _httpClient = httpClient;
            _searchOptions = searchOptions.Value;
            _storageOptions = storageOptions.Value;
            _logger = logger;
        }

        private string DocumentsUrl =>
            $"https://discoveryengine.googleapis.com/v1/projects/{_searchOptions.ProjectId}/locations/{_searchOptions.Location}/collections/default_collection/dataStores/{_searchOptions.DataStoreId}/branches/default_branch/documents";

        public async Task<IReadOnlyList<KnowledgeBaseDocument>> ListAsync(CancellationToken cancellationToken = default)
        {
            var access = await GetAccessAsync(cancellationToken);

            var documentsTask = ListIndexedDocumentsAsync(access, cancellationToken);
            var objectsTask = ListStoredObjectsAsync(access, cancellationToken);
            await Task.WhenAll(documentsTask, objectsTask);

            var objectsByUri = (await objectsTask).ToDictionary(stored => $"gs://{_storageOptions.Bucket}/{stored.Name}");

            return (await documentsTask)
                .Select(document =>
                {
                    var stored = document.Content?.Uri is { } uri ? objectsByUri.GetValueOrDefault(uri) : null;
                    return new KnowledgeBaseDocument(
                        document.Id,
                        document.StructData?.Title ?? document.Id,
                        stored is null ? null : Path.GetFileName(stored.Name),
                        stored?.Size,
                        document.StructData?.Uri,
                        document.StructData?.SourceType,
                        stored?.TimeCreated,
                        document.IndexTime);
                })
                .OrderBy(document => document.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public async Task<UploadedDocument> UploadAsync(DocumentUpload upload, CancellationToken cancellationToken = default)
        {
            var access = await GetAccessAsync(cancellationToken);

            // The import overwrites documents with the same id, so an id that is already indexed is refused.
            if (await DocumentExistsAsync(upload.Id, access, cancellationToken))
            {
                throw new DocumentAlreadyExistsException(upload.Id);
            }

            var objectName = $"{_storageOptions.Prefix}/{upload.Id}.pdf";
            StoredObject stored;
            try
            {
                stored = await StoreAsync(objectName, upload.Content, access, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new DocumentAlreadyExistsException(upload.Id);
            }

            try
            {
                var operation = await ImportAsync(upload, objectName, access, cancellationToken);
                return new UploadedDocument(
                    upload.Id,
                    upload.Title,
                    upload.FileName,
                    stored.Size ?? 0,
                    stored.TimeCreated ?? DateTimeOffset.UtcNow,
                    upload.SourceUrl,
                    operation.Name,
                    "indexing");
            }
            catch
            {
                // Nothing in the index points at the PDF yet, so don't leave it behind.
                await DeleteObjectAsync(objectName, access);
                throw;
            }
        }

        private async Task<List<IndexedDocument>> ListIndexedDocumentsAsync(GoogleAccess access, CancellationToken cancellationToken)
        {
            var documents = new List<IndexedDocument>();
            string? pageToken = null;
            do
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{DocumentsUrl}?pageSize=1000{PageTokenQuery(pageToken)}");
                var page = await SendAsync<DocumentPage>(request, access, cancellationToken);
                documents.AddRange(page.Documents ?? []);
                pageToken = page.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return documents;
        }

        private async Task<List<StoredObject>> ListStoredObjectsAsync(GoogleAccess access, CancellationToken cancellationToken)
        {
            var prefix = Uri.EscapeDataString($"{_storageOptions.Prefix}/");
            var objects = new List<StoredObject>();
            string? pageToken = null;
            do
            {
                var url = $"{StorageApi}/b/{_storageOptions.Bucket}/o?prefix={prefix}&fields=nextPageToken,items(name,size,timeCreated){PageTokenQuery(pageToken)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var page = await SendAsync<ObjectPage>(request, access, cancellationToken);
                objects.AddRange(page.Items ?? []);
                pageToken = page.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return objects;
        }

        private async Task<bool> DocumentExistsAsync(string id, GoogleAccess access, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{DocumentsUrl}/{id}");
            try
            {
                await SendAsync(request, access, cancellationToken);
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        private async Task<StoredObject> StoreAsync(string objectName, Stream content, GoogleAccess access, CancellationToken cancellationToken)
        {
            // ifGenerationMatch=0 makes Cloud Storage answer 412 instead of replacing an existing PDF.
            var url = $"{StorageUploadApi}/b/{_storageOptions.Bucket}/o?uploadType=media&ifGenerationMatch=0&name={Uri.EscapeDataString(objectName)}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StreamContent(content)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(PdfMimeType);

            return await SendAsync<StoredObject>(request, access, cancellationToken);
        }

        private async Task<Operation> ImportAsync(DocumentUpload upload, string objectName, GoogleAccess access, CancellationToken cancellationToken)
        {
            var payload = new
            {
                reconciliationMode = "INCREMENTAL",
                inlineSource = new
                {
                    documents = new[]
                    {
                        new
                        {
                            id = upload.Id,
                            // title and uri are what the bot cites as the source.
                            structData = new StructData(upload.Title, upload.SourceUrl, Handbook, UploadSourceType),
                            content = new { mimeType = PdfMimeType, uri = $"gs://{_storageOptions.Bucket}/{objectName}" }
                        }
                    }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{DocumentsUrl}:import")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };

            return await SendAsync<Operation>(request, access, cancellationToken);
        }

        private async Task DeleteObjectAsync(string objectName, GoogleAccess access)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{StorageApi}/b/{_storageOptions.Bucket}/o/{Uri.EscapeDataString(objectName)}");
            try
            {
                await SendAsync(request, access, CancellationToken.None);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Could not delete gs://{Bucket}/{ObjectName} after a failed import", _storageOptions.Bucket, objectName);
            }
        }

        private async Task<T> SendAsync<T>(HttpRequestMessage request, GoogleAccess access, CancellationToken cancellationToken)
        {
            var content = await SendAsync(request, access, cancellationToken);
            return JsonSerializer.Deserialize<T>(content, JsonOptions)
                ?? throw new HttpRequestException($"{request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)} returned an empty response.");
        }

        private async Task<string> SendAsync(HttpRequestMessage request, GoogleAccess access, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
            if (access.QuotaProject is not null)
            {
                request.Headers.Add("X-Goog-User-Project", access.QuotaProject);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"{request.Method} {request.RequestUri?.GetLeftPart(UriPartial.Path)} failed with status {(int)response.StatusCode}: {content}",
                    null,
                    response.StatusCode);
            }

            return content;
        }

        private static async Task<GoogleAccess> GetAccessAsync(CancellationToken cancellationToken)
        {
            var credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
            var token = await credential.CreateScoped(Scope).UnderlyingCredential
                .GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);

            // Local user credentials need a quota project for Discovery Engine; service accounts bill their own project.
            return new GoogleAccess(token, credential.QuotaProject);
        }

        private static string PageTokenQuery(string? pageToken) =>
            string.IsNullOrEmpty(pageToken) ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}";

        private sealed record GoogleAccess(string Token, string? QuotaProject);

        private sealed record DocumentPage(List<IndexedDocument>? Documents, string? NextPageToken);

        private sealed record IndexedDocument(string Id, StructData? StructData, DocumentContent? Content, DateTimeOffset? IndexTime);

        private sealed record StructData(
            string? Title,
            string? Uri,
            string? Handbook,
            [property: JsonPropertyName("source_type")] string? SourceType);

        private sealed record DocumentContent(string? MimeType, string? Uri);

        private sealed record ObjectPage(List<StoredObject>? Items, string? NextPageToken);

        private sealed record StoredObject(string Name, long? Size, DateTimeOffset? TimeCreated);

        private sealed record Operation(string Name);
    }
}

using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using HandbotAPI.Options;
using Microsoft.Extensions.Options;

namespace HandbotAPI.Services
{
    public interface IVertexAiSearchService
    {
        Task<JsonDocument> SearchAsync(string query, int pageSize, CancellationToken cancellationToken = default);
    }

    public class VertexAiSearchService : IVertexAiSearchService
    {
        private const string Scope = "https://www.googleapis.com/auth/cloud-platform";

        private readonly HttpClient _httpClient;
        private readonly VertexAiSearchOptions _options;

        public VertexAiSearchService(HttpClient httpClient, IOptions<VertexAiSearchOptions> options)
        {
            _httpClient = httpClient;
            _options = options.Value;
        }

        public async Task<JsonDocument> SearchAsync(string query, int pageSize, CancellationToken cancellationToken = default)
        {
            var credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
            var accessToken = await credential.CreateScoped(Scope).UnderlyingCredential
                .GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);

            var url = $"https://discoveryengine.googleapis.com/v1/projects/{_options.ProjectId}/locations/{_options.Location}/collections/default_collection/dataStores/{_options.DataStoreId}/servingConfigs/default_search:search";

            var payload = JsonSerializer.Serialize(new
            {
                query,
                pageSize,
                // The data store is chunked (layout parser), so DOCUMENTS mode returns no text; CHUNKS returns the passages for RAG.
                contentSearchSpec = new
                {
                    searchResultMode = "CHUNKS",
                    chunkSpec = new { numPreviousChunks = 1, numNextChunks = 1 }
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Add("X-Goog-User-Project", _options.ProjectId);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Vertex AI Search request failed with status {(int)response.StatusCode}: {content}");
            }

            return JsonDocument.Parse(content);
        }
    }
}

using System.Text;
using System.Text.Json;
using HandbotAPI.Options;
using Microsoft.Extensions.Options;

namespace HandbotAPI.Services
{
    public interface IAnthropicAnswerService
    {
        Task<string> AnswerAsync(string question, string context, CancellationToken cancellationToken = default);
    }

    public class AnthropicAnswerService : IAnthropicAnswerService
    {
        private readonly HttpClient _httpClient;
        private readonly IAnthropicApiKeyProvider _apiKeyProvider;
        private readonly AnthropicOptions _options;

        public AnthropicAnswerService(HttpClient httpClient, IAnthropicApiKeyProvider apiKeyProvider, IOptions<AnthropicOptions> options)
        {
            _httpClient = httpClient;
            _apiKeyProvider = apiKeyProvider;
            _options = options.Value;
        }

        public async Task<string> AnswerAsync(string question, string context, CancellationToken cancellationToken = default)
        {
            var apiKey = await _apiKeyProvider.GetApiKeyAsync(cancellationToken);

            var payload = JsonSerializer.Serialize(new
            {
                model = _options.Model,
                max_tokens = _options.MaxTokens,
                system = "Answer the user's question using ONLY the provided context. If the answer is not in the context, say you don't know.",
                messages = new[]
                {
                    new { role = "user", content = $"Context:\n{context}\n\nQuestion: {question}" }
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Anthropic request failed with status {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        }
    }
}

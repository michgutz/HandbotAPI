using Google.Cloud.SecretManager.V1;
using HandbotAPI.Options;
using Microsoft.Extensions.Options;

namespace HandbotAPI.Services
{
    public interface IAnthropicApiKeyProvider
    {
        Task<string> GetApiKeyAsync(CancellationToken cancellationToken = default);
    }

    public class GoogleSecretManagerAnthropicApiKeyProvider : IAnthropicApiKeyProvider
    {
        private readonly AnthropicOptions _options;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private string? _cachedApiKey;

        public GoogleSecretManagerAnthropicApiKeyProvider(IOptions<AnthropicOptions> options)
        {
            _options = options.Value;
        }

        public async Task<string> GetApiKeyAsync(CancellationToken cancellationToken = default)
        {
            if (_cachedApiKey is not null)
            {
                return _cachedApiKey;
            }

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (_cachedApiKey is null)
                {
                    var client = await SecretManagerServiceClient.CreateAsync(cancellationToken);
                    var secretVersionName = new SecretVersionName(_options.ProjectId, _options.ApiKeySecretId, _options.ApiKeySecretVersion);
                    var response = await client.AccessSecretVersionAsync(secretVersionName, cancellationToken);
                    _cachedApiKey = response.Payload.Data.ToStringUtf8().Trim();
                }

                return _cachedApiKey;
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}

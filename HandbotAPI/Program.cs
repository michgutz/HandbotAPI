using Anthropic;
using Anthropic.Vertex;
using Google.Cloud.SecretManager.V1;
using HandbotAPI.Options;
using HandbotAPI.Services;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.Configure<VertexAiSearchOptions>(builder.Configuration.GetSection(VertexAiSearchOptions.SectionName));
builder.Services.AddHttpClient<IVertexAiSearchService, VertexAiSearchService>();

builder.Services.Configure<ClaudeOptions>(builder.Configuration.GetSection(ClaudeOptions.SectionName));
builder.Services.AddSingleton<IAnthropicClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<ClaudeOptions>>().Value;
    if (string.IsNullOrEmpty(options.ApiKeySecret))
    {
        return new AnthropicVertexClient(new AnthropicVertexCredentials(options.Region, options.ProjectId));
    }

    // Direct Anthropic API: the key lives in Secret Manager and is read with the backend's Google credentials.
    var secret = SecretManagerServiceClient.Create().AccessSecretVersion(
        new SecretVersionName(options.ProjectId, options.ApiKeySecret, "latest"));
    return new AnthropicClient { ApiKey = secret.Payload.Data.ToStringUtf8() };
});
builder.Services.AddTransient<IKnowledgeBaseAnswerService, KnowledgeBaseAnswerService>();

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();

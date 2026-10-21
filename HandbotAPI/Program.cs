using HandbotAPI.Options;
using HandbotAPI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.Configure<DocumentStorageOptions>(builder.Configuration.GetSection(DocumentStorageOptions.SectionName));
builder.Services.AddHttpClient<IDocumentService, DocumentService>();

builder.Services.Configure<VertexAiSearchOptions>(builder.Configuration.GetSection(VertexAiSearchOptions.SectionName));
builder.Services.AddHttpClient<IVertexAiSearchService, VertexAiSearchService>();

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

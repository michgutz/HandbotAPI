using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using HandbotAPI.Options;
using Microsoft.Extensions.Options;

namespace HandbotAPI.Services
{
    public record KnowledgeBaseSource(int Id, string Title, string? Url, int? PageStart, int? PageEnd);

    public record KnowledgeBaseAnswer(string Answer, IReadOnlyList<KnowledgeBaseSource> Sources);

    public interface IKnowledgeBaseAnswerService
    {
        Task<KnowledgeBaseAnswer> AskAsync(string question, int sourceCount, CancellationToken cancellationToken = default);
    }

    public class KnowledgeBaseAnswerService : IKnowledgeBaseAnswerService
    {
        private const string SystemPrompt = """
            You are Handbot, Tech9's internal assistant. Answer the employee's question using only the handbook excerpts in the <source> tags.
            Cite the sources you rely on by id in brackets, e.g. [1] or [2][3].
            If the excerpts don't answer the question, say so plainly instead of guessing.
            Reply in the same language as the question.
            """;

        private const string NotFoundAnswer = "I couldn't find anything about that in the Tech9 handbooks.";

        private readonly IVertexAiSearchService _searchService;
        private readonly IAnthropicClient _claude;
        private readonly ClaudeOptions _options;

        public KnowledgeBaseAnswerService(IVertexAiSearchService searchService, IAnthropicClient claude, IOptions<ClaudeOptions> options)
        {
            _searchService = searchService;
            _claude = claude;
            _options = options.Value;
        }

        public async Task<KnowledgeBaseAnswer> AskAsync(string question, int sourceCount, CancellationToken cancellationToken = default)
        {
            using JsonDocument searchResult = await _searchService.SearchAsync(question, sourceCount, cancellationToken);

            var chunks = searchResult.RootElement.TryGetProperty("results", out var results)
                ? results.EnumerateArray().Select(r => r.GetProperty("chunk")).ToList()
                : [];

            if (chunks.Count == 0)
            {
                return new KnowledgeBaseAnswer(NotFoundAnswer, []);
            }

            // Neighbouring chunks give Claude more context, but skip any that already appear as a result or in an earlier source.
            var emitted = chunks.Select(c => c.GetProperty("name").GetString()!).ToHashSet();
            var sources = new List<KnowledgeBaseSource>();
            var context = new StringBuilder();

            foreach (var chunk in chunks)
            {
                var source = ToSource(sources.Count + 1, chunk);
                sources.Add(source);

                context.Append($"<source id=\"{source.Id}\" title=\"{source.Title}\">\n");
                AppendNeighbours(context, chunk, "previousChunks", emitted);
                context.Append(chunk.GetProperty("content").GetString()).Append('\n');
                AppendNeighbours(context, chunk, "nextChunks", emitted);
                context.Append("</source>\n");
            }

            var response = await _claude.Messages.Create(new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                System = SystemPrompt,
                OutputConfig = new OutputConfig { Effort = Effort.Medium },
                Messages = [new UserMessageParam { Content = $"{context}\n<question>{question}</question>" }],
            }, cancellationToken);

            if (response.StopReason == "refusal")
            {
                return new KnowledgeBaseAnswer("Claude declined to answer this question.", sources);
            }

            var answer = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            return new KnowledgeBaseAnswer(answer, sources);
        }

        private static KnowledgeBaseSource ToSource(int id, JsonElement chunk)
        {
            var metadata = chunk.GetProperty("documentMetadata");
            metadata.TryGetProperty("structData", out var structData);

            string? Field(string name) =>
                structData.ValueKind == JsonValueKind.Object && structData.TryGetProperty(name, out var value)
                    ? value.GetString()
                    : metadata.TryGetProperty(name, out var fallback) ? fallback.GetString() : null;

            int? pageStart = null, pageEnd = null;
            if (chunk.TryGetProperty("pageSpan", out var pageSpan))
            {
                pageStart = pageSpan.GetProperty("pageStart").GetInt32();
                pageEnd = pageSpan.GetProperty("pageEnd").GetInt32();
            }

            return new KnowledgeBaseSource(id, Field("title") ?? "Untitled", Field("uri"), pageStart, pageEnd);
        }

        private static void AppendNeighbours(StringBuilder context, JsonElement chunk, string direction, HashSet<string> emitted)
        {
            if (!chunk.TryGetProperty("chunkMetadata", out var chunkMetadata) || !chunkMetadata.TryGetProperty(direction, out var neighbours))
            {
                return;
            }

            foreach (var neighbour in neighbours.EnumerateArray())
            {
                if (emitted.Add(neighbour.GetProperty("name").GetString()!))
                {
                    context.Append(neighbour.GetProperty("content").GetString()).Append('\n');
                }
            }
        }
    }
}

using System.Text;
using System.Text.Json;
using HandbotAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace HandbotAPI.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class KnowledgeBaseController : ControllerBase
    {
        private readonly IVertexAiSearchService _searchService;
        private readonly IAnthropicAnswerService _answerService;
        private readonly ILogger<KnowledgeBaseController> _logger;

        public KnowledgeBaseController(IVertexAiSearchService searchService, IAnthropicAnswerService answerService, ILogger<KnowledgeBaseController> logger)
        {
            _searchService = searchService;
            _answerService = answerService;
            _logger = logger;
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string query, [FromQuery] int pageSize = 10, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest("Query parameter is required.");
            }

            try
            {
                using JsonDocument result = await _searchService.SearchAsync(query, pageSize, cancellationToken);
                return Ok(result.RootElement.Clone());
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Vertex AI Search request failed for query {Query}", query);
                return StatusCode(StatusCodes.Status502BadGateway, "Knowledge base search failed.");
            }
        }

        [HttpGet("ask")]
        public async Task<IActionResult> Ask([FromQuery] string question, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                return BadRequest("Question parameter is required.");
            }

            try
            {
                using JsonDocument searchResult = await _searchService.SearchAsync(question, 5, cancellationToken);

                JsonElement? resultsElement = null;
                var context = new StringBuilder();
                if (searchResult.RootElement.TryGetProperty("results", out var results))
                {
                    resultsElement = results.Clone();
                    foreach (var result in results.EnumerateArray())
                    {
                        if (result.TryGetProperty("document", out var doc) &&
                            doc.TryGetProperty("derivedStructData", out var data))
                        {
                            if (data.TryGetProperty("title", out var title))
                            {
                                context.AppendLine($"Source: {title.GetString()}");
                            }

                            if (data.TryGetProperty("extractive_answers", out var answers))
                            {
                                foreach (var answerElement in answers.EnumerateArray())
                                {
                                    if (answerElement.TryGetProperty("content", out var answerContent))
                                    {
                                        context.AppendLine(answerContent.GetString());
                                    }
                                }
                            }

                            if (data.TryGetProperty("extractive_segments", out var segments))
                            {
                                foreach (var segment in segments.EnumerateArray())
                                {
                                    if (segment.TryGetProperty("content", out var segmentContent))
                                    {
                                        context.AppendLine(segmentContent.GetString());
                                    }
                                }
                            }

                            if (data.TryGetProperty("snippets", out var snippets))
                            {
                                foreach (var snippet in snippets.EnumerateArray())
                                {
                                    if (snippet.TryGetProperty("snippet", out var snippetText))
                                    {
                                        context.AppendLine(snippetText.GetString());
                                    }
                                }
                            }

                            context.AppendLine();
                        }
                    }
                }

                var answer = await _answerService.AnswerAsync(question, context.ToString(), cancellationToken);
                return Ok(new { answer, results = resultsElement });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Knowledge base question answering failed for question {Question}", question);
                return StatusCode(StatusCodes.Status502BadGateway, "Knowledge base question answering failed.");
            }
        }
    }
}

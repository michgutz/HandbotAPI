using System.Text.Json;
using Anthropic.Exceptions;
using HandbotAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace HandbotAPI.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class KnowledgeBaseController : ControllerBase
    {
        private readonly IVertexAiSearchService _searchService;
        private readonly IKnowledgeBaseAnswerService _answerService;
        private readonly ILogger<KnowledgeBaseController> _logger;

        public KnowledgeBaseController(IVertexAiSearchService searchService, IKnowledgeBaseAnswerService answerService, ILogger<KnowledgeBaseController> logger)
        {
            _searchService = searchService;
            _answerService = answerService;
            _logger = logger;
        }

        [HttpGet("ask")]
        public async Task<IActionResult> Ask([FromQuery] string query, [FromQuery] int sources = 5, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return BadRequest("Query parameter is required.");
            }

            if (sources is < 1 or > 10)
            {
                return BadRequest("Sources must be between 1 and 10.");
            }

            try
            {
                return Ok(await _answerService.AskAsync(query, sources, cancellationToken));
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Vertex AI Search request failed while answering a question");
                return StatusCode(StatusCodes.Status502BadGateway, "Knowledge base search failed.");
            }
            catch (AnthropicRateLimitException ex)
            {
                _logger.LogError(ex, "Claude returned 429");
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Claude is out of quota or rate limited.");
            }
            catch (AnthropicException ex)
            {
                _logger.LogError(ex, "Claude request failed");
                return StatusCode(StatusCodes.Status502BadGateway, "Answer generation failed.");
            }
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
    }
}

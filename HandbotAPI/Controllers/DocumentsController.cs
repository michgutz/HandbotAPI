using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HandbotAPI.Models;
using HandbotAPI.Options;
using HandbotAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HandbotAPI.Controllers
{
    /// <summary>The PDFs in the knowledge base, for the dashboard. Errors come back as <c>{ "message": "..." }</c>.</summary>
    [ApiController]
    [Route("[controller]")]
    public partial class DocumentsController : ControllerBase
    {
        private const int MaxTitleLength = 255;
        private const int MaxIdLength = 63;
        /// <summary>Every PDF starts with these bytes; the declared content type alone is not trusted.</summary>
        private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();

        private readonly IDocumentService _documentService;
        private readonly DocumentStorageOptions _storageOptions;
        private readonly ILogger<DocumentsController> _logger;

        public DocumentsController(IDocumentService documentService, IOptions<DocumentStorageOptions> storageOptions, ILogger<DocumentsController> logger)
        {
            _documentService = documentService;
            _storageOptions = storageOptions.Value;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> List(CancellationToken cancellationToken = default)
        {
            try
            {
                return Ok(await _documentService.ListAsync(cancellationToken));
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Listing knowledge base documents failed");
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "The knowledge base documents could not be listed." });
            }
        }

        /// <summary>Stores the PDF and starts indexing it; it shows up in <c>GET /Documents</c> once the import finishes.</summary>
        [HttpPost]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> Upload([FromForm] UploadDocumentRequest request, CancellationToken cancellationToken = default)
        {
            var file = request.File;
            if (file is null)
            {
                return BadRequest(new { message = "Send the PDF in the 'file' field." });
            }

            if (file.Length == 0)
            {
                return BadRequest(new { message = "The file is empty." });
            }

            if (file.Length > _storageOptions.MaxUploadBytes)
            {
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { message = $"The file is larger than the {_storageOptions.MaxUploadBytes} byte limit." });
            }

            if (!await StartsLikePdfAsync(file, cancellationToken))
            {
                return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { message = "The file is not a valid PDF." });
            }

            var fileName = Path.GetFileName(file.FileName);
            var title = string.IsNullOrWhiteSpace(request.Title) ? Path.GetFileNameWithoutExtension(fileName).Trim() : request.Title.Trim();
            if (title.Length == 0 || title.Length > MaxTitleLength)
            {
                return BadRequest(new { message = $"The title must have between 1 and {MaxTitleLength} characters." });
            }

            var id = string.IsNullOrWhiteSpace(request.Id) ? Slugify(title) : request.Id.Trim();
            if (!DocumentIdPattern().IsMatch(id))
            {
                return BadRequest(new { message = $"The id must be lowercase letters, numbers, '-' or '_' (up to {MaxIdLength}), starting with a letter or number." });
            }

            var sourceUrl = string.IsNullOrWhiteSpace(request.SourceUrl) ? null : request.SourceUrl.Trim();
            if (sourceUrl is not null && !IsHttpUrl(sourceUrl))
            {
                return BadRequest(new { message = "The sourceUrl must be an http(s) URL." });
            }

            try
            {
                await using var content = file.OpenReadStream();
                var uploaded = await _documentService.UploadAsync(new DocumentUpload(id, title, fileName, sourceUrl, content), cancellationToken);
                return Accepted(uploaded);
            }
            catch (DocumentAlreadyExistsException ex)
            {
                return Conflict(new { message = ex.Message });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Uploading document {DocumentId} failed", id);
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "The document could not be added to the knowledge base." });
            }
        }

        private static async Task<bool> StartsLikePdfAsync(IFormFile file, CancellationToken cancellationToken)
        {
            await using var stream = file.OpenReadStream();
            var header = new byte[PdfSignature.Length];
            await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
            return header.AsSpan().SequenceEqual(PdfSignature);
        }

        private static bool IsHttpUrl(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

        /// <summary>"Política de Viajes (v2)" becomes "politica-de-viajes-v2".</summary>
        private static string Slugify(string value)
        {
            var slug = new StringBuilder();
            foreach (var c in value.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsAsciiLetterOrDigit(c))
                {
                    slug.Append(char.ToLowerInvariant(c));
                }
                else if (slug.Length > 0 && slug[^1] != '-')
                {
                    slug.Append('-');
                }
            }

            var id = slug.ToString(0, Math.Min(slug.Length, MaxIdLength)).Trim('-');
            return id.Length > 0 ? id : $"document-{Guid.NewGuid():N}"[..17];
        }

        [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,62}$")]
        private static partial Regex DocumentIdPattern();
    }
}

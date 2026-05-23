using System.ComponentModel.DataAnnotations;
using System.Text;
// using Microsoft.AspNetCore.Authorization; // COMMENTED OUT - Auth disabled
using Microsoft.AspNetCore.Mvc;
using Mapping_LIA.Services.AreaMapper;

namespace Mapping_LIA.Controllers;

/// <summary>
/// Public mapping API for turning raw competence names into reviewable mapped records.
/// </summary>
/// <remarks>
/// Authentication is currently disabled for the current internal setup. If this API
/// is exposed beyond trusted users, restore authorization before relying on the
/// request limits here as the only protection.
/// </remarks>
[ApiController]
[Route("api/area-mapper")]
// [Authorize] // COMMENTED OUT - Auth disabled
public class AreaMapperController : ControllerBase
{
    private const int MaxBatchLines = 1000;
    private const long MaxPlainTextBodyBytes = 256 * 1024;
    private const long MaxUploadFileBytes = 512 * 1024;

    private readonly IAreaMapperService _areaMapperService;
    private readonly ICompetenceMappingQueue _queue;

    public AreaMapperController(IAreaMapperService areaMapperService, ICompetenceMappingQueue queue)
    {
        _areaMapperService = areaMapperService;
        _queue = queue;
    }

    public record MapRequest(
        [Required]
        [property: System.Text.Json.Serialization.JsonPropertyName("competence")]
        string Competence
    );

    /// <summary>
    /// Maps one competence immediately and stores it as pending review.
    /// </summary>
    /// <remarks>
    /// New entries are not auto-imported to Profiler. They stay in PendingReview
    /// so a person can approve, reject, or adjust the LLM's suggested mapping.
    /// </remarks>
    [HttpPost("map")]
    [Consumes("application/json")]
    public async Task<ActionResult<MapResponse>> Map([FromBody] MapRequest req, CancellationToken ct)
    {
        var input = (req.Competence ?? string.Empty)
            .Replace("\r", "")
            .Replace("\n", "")
            .Trim();

        if (string.IsNullOrWhiteSpace(input))
            return BadRequest("Provide 'competence'.");

        var result = await _areaMapperService.MapCompetenceAsync(input, ct);
        if (!result.Success)
            return BadRequest(result.ErrorMessage);

        return StatusCode(201, result.Response);
    }

    /// <summary>
    /// Queues a plain-text batch with one competence per line.
    /// </summary>
    /// <remarks>
    /// The limits are intentionally small because every accepted line may result
    /// in LLM work and database writes. Larger imports should be split into
    /// batches so one request cannot monopolize the worker.
    /// </remarks>
    [HttpPost("map-lines")]
    [Consumes("text/plain")]
    public async Task<IActionResult> MapLines()
    {
        if (Request.ContentLength > MaxPlainTextBodyBytes)
            return StatusCode(413, $"Body is too large. Maximum size is {MaxPlainTextBodyBytes / 1024} KB.");

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var raw = await reader.ReadToEndAsync();

        if (Encoding.UTF8.GetByteCount(raw) > MaxPlainTextBodyBytes)
            return StatusCode(413, $"Body is too large. Maximum size is {MaxPlainTextBodyBytes / 1024} KB.");

        var lines = raw
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
            return BadRequest("Body was empty. Send one competence per line.");

        if (lines.Length > MaxBatchLines)
            return StatusCode(413, $"Too many competences in one request. Maximum is {MaxBatchLines} lines.");

        var jobId = _queue.Enqueue(lines);
        return Accepted(new { JobId = jobId });
    }


    /// <summary>
    /// Handles form uploads with either one competence field or a small line-based file, this is prone to bugs, using plain text works better.
    /// </summary>
    /// <remarks>
    /// The file limit protects the API from accidental large uploads and keeps
    /// LLM-backed batch processing predictable for the single background worker.
    /// </remarks>
    public record MapFormRequest(string? Competence, IFormFile? File);

    [HttpPost("map-form")]
    [Consumes("multipart/form-data", "application/x-www-form-urlencoded")]
    public async Task<ActionResult<IEnumerable<MapResponse>>> MapForm([FromForm] MapFormRequest req, CancellationToken ct)
    {
        var inputs = new List<string>();

        if (!string.IsNullOrWhiteSpace(req.Competence))
            inputs.Add(req.Competence!);

        if (req.File is not null && req.File.Length > 0)
        {
            if (req.File.Length > MaxUploadFileBytes)
                return StatusCode(413, $"Uploaded file is too large. Maximum size is {MaxUploadFileBytes / 1024} KB.");

            using var r = new StreamReader(req.File.OpenReadStream(), Encoding.UTF8);
            while (!r.EndOfStream)
            {
                var line = (await r.ReadLineAsync())?.Trim();
                if (!string.IsNullOrWhiteSpace(line))
                    inputs.Add(line!);

                if (inputs.Count > MaxBatchLines)
                    return StatusCode(413, $"Too many competences in one request. Maximum is {MaxBatchLines} lines.");
            }
        }

        if (inputs.Count == 0)
            return BadRequest("Provide 'competence' or upload a file with one competence per line.");

        var batchResult = await _areaMapperService.MapCompetencesAsync(inputs, ct);

        if (batchResult.Errors.Count > 0 && batchResult.Results.Count == 0)
            return BadRequest(string.Join("; ", batchResult.Errors));

        // 207 Multi-Status: partial success (some succeeded, some failed)
        if (batchResult.Errors.Count > 0)
            return StatusCode(207, new { Results = batchResult.Results, Errors = batchResult.Errors });

        return StatusCode(201, batchResult.Results);
    }

    /// <summary>
    /// Returns the current status for an asynchronous line-mapping job.
    /// </summary>
    /// <remarks>
    /// Completed job status is retained only for a short period by the in-memory
    /// queue, so old job IDs can return 404 after cleanup.
    /// </remarks>
    [HttpGet("map-lines/{jobId:guid}")]
    public IActionResult GetMapLinesStatus(Guid jobId)
    {
        return _queue.TryGet(jobId, out var status)
            ? Ok(status)
            : NotFound("Job not found.");
    }
}
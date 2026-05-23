using System.ComponentModel.DataAnnotations;
// using Microsoft.AspNetCore.Authorization; // COMMENTED OUT - Auth disabled
using Microsoft.AspNetCore.Mvc;
using Mapping_LIA.Services.Review;

namespace Mapping_LIA.Controllers;

/// <summary>
/// Review API for the human approval workflow between LLM mapping and Profiler import.
/// </summary>
/// <remarks>
/// Most mutations enforce status transitions in <see cref="IReviewService"/>.
/// Keep controller changes thin so the frontend and future docs share one set of
/// business rules.
/// </remarks>
[ApiController]
[Route("api/review")]
// [Authorize] // COMMENTED OUT - Auth disabled
public class ReviewController : ControllerBase
{
    private readonly IReviewService _reviewService;

    public ReviewController(IReviewService reviewService)
    {
        _reviewService = reviewService;
    }

    /// <summary>
    /// Get aggregate counts per status for dashboard KPIs.
    /// Archive includes both Seeded Legacy and Imported to Profiler competences.
    /// </summary>
    [HttpGet("counts")]
    public async Task<ActionResult<ReviewCounts>> GetCounts(CancellationToken ct = default)
    {
        var counts = await _reviewService.GetCountsAsync(ct);
        return Ok(counts);
    }

    /// <summary>
    /// Unified archive endpoint that returns all competences that are part of
    /// the legacy/archive catalogue, including both originally seeded legacy
    /// competences and those that have been approved and then imported/completed.
    /// </summary>
    [HttpGet("archive")]
    public async Task<ActionResult<IEnumerable<CompetenceListItem>>> GetArchive(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (take <= 0 || take > 1000)
            take = 100;
        if (skip < 0)
            skip = 0;

        var competences = await _reviewService.GetArchiveAsync(skip, take, ct);
        return Ok(competences);
    }

    // List Legacy/Imported (seeded) Competences
    [HttpGet("legacy")]
    public async Task<ActionResult<IEnumerable<CompetenceListItem>>> GetLegacyImported(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (take <= 0 || take > 1000)
            take = 100;
        if (skip < 0)
            skip = 0;

        var competences = await _reviewService.GetLegacyImportedAsync(skip, take, ct);
        return Ok(competences);
    }

    // List Imported/Completed Competences
    [HttpGet("imported")]
    public async Task<ActionResult<IEnumerable<CompetenceListItem>>> GetImportedCompleted(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (take <= 0 || take > 1000)
            take = 100;
        if (skip < 0)
            skip = 0;

        var competences = await _reviewService.GetImportedCompletedAsync(skip, take, ct);
        return Ok(competences);
    }

    // List Pending Review Competences
    [HttpGet("pending")]
    public async Task<ActionResult<IEnumerable<CompetenceListItem>>> GetPending(
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        if (take <= 0 || take > 1000)
            take = 100;
        if (skip < 0)
            skip = 0;

        var competences = await _reviewService.GetPendingReviewAsync(skip, take, ct);
        return Ok(competences);
    }

    // Get Single Competence Details
    [HttpGet("{competenceId}")]
    public async Task<ActionResult<CompetenceDetail>> GetById(
        Guid competenceId,
        CancellationToken ct = default)
    {
        var detail = await _reviewService.GetByIdAsync(competenceId, ct);

        if (detail == null)
            return NotFound($"Competence with ID {competenceId} not found.");

        return Ok(detail);
    }

    [HttpGet("approved")]
    public async Task<ActionResult<IEnumerable<CompetenceListItem>>> GetApproved(
   [FromQuery] int skip = 0,
   [FromQuery] int take = 50,
   CancellationToken ct = default)
    {
        if (take <= 0 || take > 1000)
            take = 100;
        if (skip < 0)
            skip = 0;

        var competences = await _reviewService.GetApprovedAsync(skip, take, ct);
        return Ok(competences);
    }

    [HttpGet("rejected")]
    public async Task<ActionResult<IEnumerable<CompetenceListItem>>> GetRejected(
    [FromQuery] int skip = 0,
    [FromQuery] int take = 50,
    CancellationToken ct = default)
    {
        if (take <= 0 || take > 1000)
            take = 100;
        if (skip < 0)
            skip = 0;

        var competences = await _reviewService.GetRejectedAsync(skip, take, ct);
        return Ok(competences);
    }

    // Approve Competence
    public record ApproveRequest(string? ReviewNotes);

    [HttpPost("{competenceId}/approve")]
    public async Task<ActionResult> Approve(
        Guid competenceId,
        [FromBody] ApproveRequest? request = null,
        CancellationToken ct = default)
    {
        var result = await _reviewService.ApproveAsync(competenceId, request?.ReviewNotes, ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("not found") == true)
                return NotFound(result.ErrorMessage);
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new { Message = "Competence approved successfully", CompetenceId = competenceId });
    }

    // Bulk approve PendingReview competences
    public record BulkApproveRequest([Required] Guid[] CompetenceIds, string? ReviewNotes);

    [HttpPost("approve/bulk")]
    public async Task<ActionResult> BulkApprove(
        [FromBody] BulkApproveRequest request,
        CancellationToken ct = default)
    {
        if (request.CompetenceIds == null || request.CompetenceIds.Length == 0)
        {
            return BadRequest("CompetenceIds is required and must contain at least one ID.");
        }

        var result = await _reviewService.BulkApproveAsync(request.CompetenceIds, request.ReviewNotes, ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("not found") == true)
                return NotFound(result.ErrorMessage);
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new
        {
            Message = result.ErrorMessage ?? "Competences approved successfully"
        });
    }

    // Reject Competence
    public record RejectRequest(
        [Required]
        string ReviewNotes
    );

    [HttpPost("{competenceId}/reject")]
    public async Task<ActionResult> Reject(
        Guid competenceId,
        [FromBody] RejectRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ReviewNotes))
            return BadRequest("ReviewNotes is required for rejection.");

        var result = await _reviewService.RejectAsync(competenceId, request.ReviewNotes, ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("not found") == true)
                return NotFound(result.ErrorMessage);
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new { Message = "Competence rejected successfully", CompetenceId = competenceId });
    }

    // Bulk reject PendingReview competences
    public record BulkRejectRequest(
        [Required] Guid[] CompetenceIds,
        [Required] string ReviewNotes
    );

    [HttpPost("reject/bulk")]
    public async Task<ActionResult> BulkReject(
        [FromBody] BulkRejectRequest request,
        CancellationToken ct = default)
    {
        if (request.CompetenceIds == null || request.CompetenceIds.Length == 0)
        {
            return BadRequest("CompetenceIds is required and must contain at least one ID.");
        }

        if (string.IsNullOrWhiteSpace(request.ReviewNotes))
        {
            return BadRequest("ReviewNotes is required for rejection.");
        }

        var result = await _reviewService.BulkRejectAsync(request.CompetenceIds, request.ReviewNotes, ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("not found") == true)
                return NotFound(result.ErrorMessage);
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new
        {
            Message = result.ErrorMessage ?? "Competences rejected successfully"
        });
    }

    // Get metadata (areas, categories, subcategories) for categorization editor
    [HttpGet("metadata")]
    public async Task<ActionResult> GetMetadata(CancellationToken ct = default)
    {
        var metadata = await _reviewService.GetMetadataAsync(ct);
        return Ok(metadata);
    }

    // Update categorization for a pending competence (optional: also update competence name with duplicate check)
    public record UpdateCategorizationRequest(
        [Required] Guid AreaId,
        Guid? CategoryId,
        Guid? SubcategoryId,
        string? Name
    );

    [HttpPatch("{competenceId}/update-categorization")]
    public async Task<ActionResult> UpdateCategorization(
        Guid competenceId,
        [FromBody] UpdateCategorizationRequest request,
        CancellationToken ct = default)
    {
        var result = await _reviewService.UpdateCategorizationAsync(
            competenceId,
            request.AreaId,
            request.CategoryId,
            request.SubcategoryId,
            request.Name,
            ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("not found") == true)
                return NotFound(result.ErrorMessage);
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new
        {
            Message = "Categorization updated successfully",
            CompetenceId = competenceId
        });
    }

    // Controller to assign competence to others
    public record AssignToOtherRequest(string? ReviewNotes);
    [HttpPost("{competenceId}/assign-other")]
    public async Task<ActionResult> AssignToOtherArea(
        Guid competenceId,
        [FromBody] AssignToOtherRequest? request = null,
        CancellationToken ct = default)
    {
        var result = await _reviewService.AssignToOtherAreaAsync(competenceId, request?.ReviewNotes, ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("not found") == true)
                return NotFound(result.ErrorMessage);
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new
        {
            Message = "Competence assigned to area 'Other' successfully",
            CompetenceId = competenceId
        });
    }

    // Bulk mark Approved competences as Imported/Completed
    public record BulkImportedRequest([Required] Guid[] CompetenceIds);

    [HttpPost("imported/bulk")]
    public async Task<ActionResult> BulkMarkImported(
        [FromBody] BulkImportedRequest request,
        CancellationToken ct = default)
    {
        if (request.CompetenceIds == null || request.CompetenceIds.Length == 0)
        {
            return BadRequest("CompetenceIds is required and must contain at least one ID.");
        }

        var result = await _reviewService.BulkMarkImportedAsync(request.CompetenceIds, ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("not found") == true)
                return NotFound(result.ErrorMessage);
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new
        {
            Message = result.ErrorMessage ?? "Competences marked as Imported/Completed successfully"
        });
    }

    // Bulk move Approved competences back to PendingReview
    public record BulkMoveToPendingRequest([Required] Guid[] CompetenceIds);

    [HttpPost("pending/bulk")]
    public async Task<ActionResult> BulkMoveToPending(
        [FromBody] BulkMoveToPendingRequest request,
        CancellationToken ct = default)
    {
        if (request.CompetenceIds == null || request.CompetenceIds.Length == 0)
        {
            return BadRequest("CompetenceIds is required and must contain at least one ID.");
        }

        var result = await _reviewService.BulkMoveToPendingAsync(request.CompetenceIds, ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("not found") == true)
                return NotFound(result.ErrorMessage);
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new
        {
            Message = result.ErrorMessage ?? "Competences moved back to Pending successfully"
        });
    }

    // Bulk delete competences (Approved only)
    public record BulkDeleteRequest([Required] Guid[] CompetenceIds);

    [HttpPost("delete/bulk")]
    public async Task<ActionResult> BulkDelete(
        [FromBody] BulkDeleteRequest request,
        CancellationToken ct = default)
    {
        if (request.CompetenceIds == null || request.CompetenceIds.Length == 0)
        {
            return BadRequest("CompetenceIds is required and must contain at least one ID.");
        }

        var result = await _reviewService.BulkDeleteAsync(request.CompetenceIds, ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("not found") == true)
                return NotFound(result.ErrorMessage);
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new
        {
            Message = result.ErrorMessage ?? "Competences deleted successfully"
        });
    }

    /// <summary>
    /// Deletes one approved competence so it can be mapped again.
    /// </summary>
    /// <remarks>
    /// The service only permits Approved rows here. Pending review rows, rejected
    /// rows, and archive/import rows are preserved to avoid losing review history
    /// or seeded catalogue data by accident.
    /// </remarks>
    [HttpDelete("{competenceId}")]
    public async Task<ActionResult> Delete(
        Guid competenceId,
        CancellationToken ct = default)
    {
        var result = await _reviewService.DeleteAsync(competenceId, ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("not found") == true)
                return NotFound(result.ErrorMessage);
            return BadRequest(result.ErrorMessage);
        }

        return Ok(new
        {
            Message = "Competence deleted successfully",
            CompetenceId = competenceId
        });
    }
}
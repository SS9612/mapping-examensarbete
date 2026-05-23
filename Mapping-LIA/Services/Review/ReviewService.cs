using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mapping_LIA.Data;
using Mapping_LIA.Entities;
using Mapping_LIA.Services.Normalization;

namespace Mapping_LIA.Services.Review;

/// <summary>
/// Business rules for the review dashboard and Profiler import lifecycle.
/// </summary>
/// <remarks>
/// Controllers should stay thin and call this service for status transitions so
/// single-row and bulk operations keep the same rules.
/// </remarks>
public class ReviewService : IReviewService
{
    private readonly ApplicationDbContext _db;
    private readonly ITextNormalizer _normalizer;
    private readonly ILogger<ReviewService> _logger;

    /// <summary>
    /// Statuses that are considered part of the archived/legacy catalogue.
    /// This includes both originally seeded legacy competences and
    /// competences that have been approved and then imported/completed.
    /// </summary>
    private static readonly CompetenceStatus[] ArchivedStatuses = new[]
    {
        CompetenceStatus.LegacyImported,
        CompetenceStatus.ImportedCompleted
    };

    private static Area? _cachedOtherArea;
    private static readonly SemaphoreSlim _otherAreaSemaphore = new SemaphoreSlim(1, 1);
    private static DateTime _otherAreaCacheTime = DateTime.MinValue;
    private static readonly TimeSpan OtherAreaCacheExpiry = TimeSpan.FromMinutes(30);

    public ReviewService(
        ApplicationDbContext db,
        ITextNormalizer normalizer,
        ILogger<ReviewService> logger)
    {
        _db = db;
        _normalizer = normalizer;
        _logger = logger;
    }
    private async Task<IEnumerable<CompetenceListItem>> GetByStatusAsync(
         CompetenceStatus status,
         int skip,
         int take,
         CancellationToken ct = default)
    {
        var competences = await _db.Competences
            .Where(c => c.Status == status)
            .Include(c => c.Area)
            .Include(c => c.Category)
            .Include(c => c.Subcategory)
            .OrderBy(c => c.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(c => new CompetenceListItem(
                c.CompetenceId,
                c.Name,
                c.Normalized,
                c.Area != null ? c.Area.Name : null,
                c.Category != null ? c.Category.Name : null,
                c.Subcategory != null ? c.Subcategory.Name : null,
                c.Confidence,
                c.MatchedType,
                c.CreatedAt,
                c.ReviewedAt,
                c.ReviewNotes
            ))
            .ToListAsync(ct);

        return competences;
    }

    /// <summary>
    /// Get all competences that are part of the archived/legacy catalogue,
    /// i.e. those whose status is one of the ArchivedStatuses.
    /// </summary>
    public async Task<IEnumerable<CompetenceListItem>> GetArchiveAsync(
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var competences = await _db.Competences
            .Where(c => ArchivedStatuses.Contains(c.Status))
            .Include(c => c.Area)
            .Include(c => c.Category)
            .Include(c => c.Subcategory)
            .OrderBy(c => c.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(c => new CompetenceListItem(
                c.CompetenceId,
                c.Name,
                c.Normalized,
                c.Area != null ? c.Area.Name : null,
                c.Category != null ? c.Category.Name : null,
                c.Subcategory != null ? c.Subcategory.Name : null,
                c.Confidence,
                c.MatchedType,
                c.CreatedAt,
                c.ReviewedAt,
                c.ReviewNotes
            ))
            .ToListAsync(ct);

        return competences;
    }

    // Get competences pending review with pagination (newest first so newly mapped items show at top)
    public async Task<IEnumerable<CompetenceListItem>> GetPendingReviewAsync(
        int skip,
        int take,
        CancellationToken ct = default)
    {
        var competences = await _db.Competences
            .Where(c => c.Status == CompetenceStatus.PendingReview)
            .Include(c => c.Area)
            .Include(c => c.Category)
            .Include(c => c.Subcategory)
            .OrderByDescending(c => c.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(c => new CompetenceListItem(
                c.CompetenceId,
                c.Name,
                c.Normalized,
                c.Area != null ? c.Area.Name : null,
                c.Category != null ? c.Category.Name : null,
                c.Subcategory != null ? c.Subcategory.Name : null,
                c.Confidence,
                c.MatchedType,
                c.CreatedAt,
                c.ReviewedAt,
                c.ReviewNotes
            ))
            .ToListAsync(ct);

        return competences;
    }

    public Task<IEnumerable<CompetenceListItem>> GetApprovedAsync(
        int skip,
        int take,
        CancellationToken ct = default)
    {
        return GetByStatusAsync(CompetenceStatus.Approved, skip, take, ct);
    }

    public Task<IEnumerable<CompetenceListItem>> GetRejectedAsync(
        int skip,
        int take,
        CancellationToken ct = default)
    {
        return GetByStatusAsync(CompetenceStatus.Rejected, skip, take, ct);
    }

    public Task<IEnumerable<CompetenceListItem>> GetLegacyImportedAsync(
        int skip,
        int take,
        CancellationToken ct = default)
    {
        return GetByStatusAsync(CompetenceStatus.LegacyImported, skip, take, ct);
    }

    public Task<IEnumerable<CompetenceListItem>> GetImportedCompletedAsync(
        int skip,
        int take,
        CancellationToken ct = default)
    {
        return GetByStatusAsync(CompetenceStatus.ImportedCompleted, skip, take, ct);
    }

    /// <summary>
    /// Get aggregate counts per status for dashboard KPIs.
    /// Archive includes both LegacyImported and ImportedCompleted.
    /// </summary>
    public async Task<ReviewCounts> GetCountsAsync(CancellationToken ct = default)
    {
        var pending = await _db.Competences
            .CountAsync(c => c.Status == CompetenceStatus.PendingReview, ct);

        var approved = await _db.Competences
            .CountAsync(c => c.Status == CompetenceStatus.Approved, ct);

        var rejected = await _db.Competences
            .CountAsync(c => c.Status == CompetenceStatus.Rejected, ct);

        var legacy = await _db.Competences
            .CountAsync(c => c.Status == CompetenceStatus.LegacyImported, ct);

        var imported = await _db.Competences
            .CountAsync(c => c.Status == CompetenceStatus.ImportedCompleted, ct);

        var archive = await _db.Competences
            .CountAsync(c => ArchivedStatuses.Contains(c.Status), ct);

        return new ReviewCounts(
            pending,
            approved,
            rejected,
            legacy,
            imported,
            archive
        );
    }

    // Get competence details by ID
    public async Task<CompetenceDetail?> GetByIdAsync(
        Guid competenceId,
        CancellationToken ct = default)
    {
        var competence = await _db.Competences
            .Include(c => c.Area)
            .Include(c => c.Category)
            .Include(c => c.Subcategory)
            .FirstOrDefaultAsync(c => c.CompetenceId == competenceId, ct);

        if (competence == null)
            return null;

        return new CompetenceDetail(
            competence.CompetenceId,
            competence.Name,
            competence.Normalized,
            competence.AreaId,
            competence.Area != null ? competence.Area.Name : null,
            competence.CategoryId,
            competence.Category != null ? competence.Category.Name : null,
            competence.SubcategoryId,
            competence.Subcategory != null ? competence.Subcategory.Name : null,
            competence.Status,
            competence.Confidence,
            competence.MatchedType,
            competence.CreatedAt,
            competence.ReviewedAt,
            competence.ReviewNotes
        );
    }

    // Approve a competence
    public async Task<ReviewResult> ApproveAsync(
        Guid competenceId,
        string? reviewNotes,
        CancellationToken ct = default)
    {
        var competence = await _db.Competences
            .FirstOrDefaultAsync(c => c.CompetenceId == competenceId, ct);

        if (competence == null)
            return new ReviewResult(false, $"Competence with ID {competenceId} not found.", null);

        if (competence.Status != CompetenceStatus.PendingReview)
            return new ReviewResult(false,
                $"Competence is not in PendingReview status. Current status: {competence.Status}",
                competence.Name);

        // Update competence status to Approved
        competence.Status = CompetenceStatus.Approved;
        competence.ReviewedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(reviewNotes))
        {
            competence.ReviewNotes = string.IsNullOrWhiteSpace(competence.ReviewNotes)
                ? reviewNotes
                : $"{competence.ReviewNotes}\n--- {reviewNotes}";
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Competence '{Name}' (ID: {CompetenceId}) approved",
            competence.Name, competence.CompetenceId);

        return new ReviewResult(true, null, competence.Name);
    }

    // Bulk approve pending competences in a single transaction
    public async Task<ReviewResult> BulkApproveAsync(
        IEnumerable<Guid> competenceIds,
        string? reviewNotes,
        CancellationToken ct = default)
    {
        var ids = competenceIds?.Distinct().ToList() ?? new List<Guid>();
        if (ids.Count == 0)
        {
            return new ReviewResult(false, "No competence IDs were provided for bulk approve.", null);
        }

        var competences = await _db.Competences
            .Where(c => ids.Contains(c.CompetenceId))
            .ToListAsync(ct);

        if (!competences.Any())
        {
            return new ReviewResult(false, "No competences found for the provided IDs.", null);
        }

        var updated = 0;
        var skipped = 0;

        foreach (var competence in competences)
        {
            if (competence.Status != CompetenceStatus.PendingReview)
            {
                skipped++;
                continue;
            }

            competence.Status = CompetenceStatus.Approved;
            competence.ReviewedAt = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(reviewNotes))
            {
                competence.ReviewNotes = string.IsNullOrWhiteSpace(competence.ReviewNotes)
                    ? reviewNotes
                    : $"{competence.ReviewNotes}\n--- {reviewNotes}";
            }

            updated++;
        }

        if (updated == 0)
        {
            return new ReviewResult(false,
                "No competences in PendingReview status were found to approve.",
                null);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Bulk approved {Updated} competences. Skipped (not PendingReview): {Skipped}.",
            updated, skipped);

        var message = skipped > 0
            ? $"Approved {updated} competences; skipped {skipped} that were not in PendingReview status."
            : null;

        return new ReviewResult(true, message, null);
    }

    // Reject a competence
    public async Task<ReviewResult> RejectAsync(
        Guid competenceId,
        string reviewNotes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reviewNotes))
            return new ReviewResult(false, "ReviewNotes is required for rejection.", null);

        var competence = await _db.Competences
            .FirstOrDefaultAsync(c => c.CompetenceId == competenceId, ct);

        if (competence == null)
            return new ReviewResult(false, $"Competence with ID {competenceId} not found.", null);

        if (competence.Status != CompetenceStatus.PendingReview)
            return new ReviewResult(false,
                $"Competence is not in PendingReview status. Current status: {competence.Status}",
                competence.Name);

        // Update competence status to Rejected
        competence.Status = CompetenceStatus.Rejected;
        competence.ReviewedAt = DateTime.UtcNow;
        var trimmedNotes = reviewNotes.Trim();
        competence.ReviewNotes = string.IsNullOrWhiteSpace(competence.ReviewNotes)
            ? trimmedNotes
            : $"{competence.ReviewNotes}\n--- {trimmedNotes}";

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Competence '{Name}' (ID: {CompetenceId}) rejected. Notes: {Notes}",
            competence.Name, competence.CompetenceId, reviewNotes);

        return new ReviewResult(true, null, competence.Name);
    }

    // Bulk reject pending competences in a single transaction
    public async Task<ReviewResult> BulkRejectAsync(
        IEnumerable<Guid> competenceIds,
        string reviewNotes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reviewNotes))
            return new ReviewResult(false, "ReviewNotes is required for bulk rejection.", null);

        var ids = competenceIds?.Distinct().ToList() ?? new List<Guid>();
        if (ids.Count == 0)
        {
            return new ReviewResult(false, "No competence IDs were provided for bulk reject.", null);
        }

        var competences = await _db.Competences
            .Where(c => ids.Contains(c.CompetenceId))
            .ToListAsync(ct);

        if (!competences.Any())
        {
            return new ReviewResult(false, "No competences found for the provided IDs.", null);
        }

        var updated = 0;
        var skipped = 0;
        var trimmedNotes = reviewNotes.Trim();

        foreach (var competence in competences)
        {
            if (competence.Status != CompetenceStatus.PendingReview)
            {
                skipped++;
                continue;
            }

            competence.Status = CompetenceStatus.Rejected;
            competence.ReviewedAt = DateTime.UtcNow;
            competence.ReviewNotes = string.IsNullOrWhiteSpace(competence.ReviewNotes)
                ? trimmedNotes
                : $"{competence.ReviewNotes}\n--- {trimmedNotes}";

            updated++;
        }

        if (updated == 0)
        {
            return new ReviewResult(false,
                "No competences in PendingReview status were found to reject.",
                null);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Bulk rejected {Updated} competences. Skipped (not PendingReview): {Skipped}.",
            updated, skipped);

        var message = skipped > 0
            ? $"Rejected {updated} competences; skipped {skipped} that were not in PendingReview status."
            : null;

        return new ReviewResult(true, message, null);
    }

    /// <summary>
    /// Looks up the special catch-all "Other" area used when reviewers disagree with the suggested mapping.
    /// </summary>
    /// <remarks>
    /// The area is seeded data rather than an enum, so this cache keeps repeated
    /// review actions cheap while still refreshing periodically if seed data is
    /// corrected during handoff.
    /// </remarks>
    private async Task<Area?> GetCachedOtherAreaAsync(CancellationToken ct)
    {
        if (_cachedOtherArea != null &&
            DateTime.UtcNow - _otherAreaCacheTime < OtherAreaCacheExpiry)
        {
            return _cachedOtherArea;
        }
        // Double-check pattern: another thread might have populated cache while we waited
        await _otherAreaSemaphore.WaitAsync(ct);
        try
        {
            if (_cachedOtherArea != null &&
                DateTime.UtcNow - _otherAreaCacheTime < OtherAreaCacheExpiry)
            {
                return _cachedOtherArea;
            }

            var otherArea = await _db.Areas
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Name == "Other", ct);

            _cachedOtherArea = otherArea;
            _otherAreaCacheTime = DateTime.UtcNow;

            return otherArea;
        }
        finally
        {
            _otherAreaSemaphore.Release();
        }
    }
    // Get metadata (areas, categories, subcategories) for categorization editor
    public async Task<MetadataResult> GetMetadataAsync(CancellationToken ct = default)
    {
        var areas = await _db.Areas
            .AsNoTracking()
            .Select(a => new AreaMetadata(a.AreaId, a.Name))
            .ToListAsync(ct);

        var categories = await _db.Categories
            .AsNoTracking()
            .Select(c => new CategoryMetadata(c.CategoryId, c.Name, c.AreaId))
            .ToListAsync(ct);

        var subcategories = await _db.Subcategories
            .AsNoTracking()
            .Select(s => new SubcategoryMetadata(s.SubcategoryId, s.Name, s.CategoryId))
            .ToListAsync(ct);

        return new MetadataResult(areas, categories, subcategories);
    }

    // Update categorization for a pending competence (optional name update with duplicate check vs all competences including legacy)
    public async Task<ReviewResult> UpdateCategorizationAsync(
        Guid competenceId,
        Guid areaId,
        Guid? categoryId,
        Guid? subcategoryId,
        string? name = null,
        CancellationToken ct = default)
    {
        var competence = await _db.Competences
            .FirstOrDefaultAsync(c => c.CompetenceId == competenceId, ct);

        if (competence == null)
            return new ReviewResult(false, $"Competence with ID {competenceId} not found.", null);

        if (competence.Status != CompetenceStatus.PendingReview)
            return new ReviewResult(false,
                $"Competence is not in PendingReview status. Current status: {competence.Status}",
                competence.Name);

        // Validate Area exists
        var area = await _db.Areas
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AreaId == areaId, ct);

        if (area == null)
            return new ReviewResult(false, $"Area with ID {areaId} not found.", competence.Name);

        // Validate Category if provided
        if (categoryId.HasValue)
        {
            var category = await _db.Categories
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.CategoryId == categoryId.Value, ct);

            if (category == null)
                return new ReviewResult(false, $"Category with ID {categoryId.Value} not found.", competence.Name);

            if (category.AreaId != areaId)
                return new ReviewResult(false,
                    $"Category '{category.Name}' does not belong to the selected area.",
                    competence.Name);
        }

        // Validate Subcategory if provided
        if (subcategoryId.HasValue)
        {
            if (!categoryId.HasValue)
                return new ReviewResult(false,
                    "Subcategory cannot be set without a category.",
                    competence.Name);

            var subcategory = await _db.Subcategories
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SubcategoryId == subcategoryId.Value, ct);

            if (subcategory == null)
                return new ReviewResult(false, $"Subcategory with ID {subcategoryId.Value} not found.", competence.Name);

            if (subcategory.CategoryId != categoryId.Value)
                return new ReviewResult(false,
                    $"Subcategory '{subcategory.Name}' does not belong to the selected category.",
                    competence.Name);
        }

        // Optional: update competence name with duplicate check (same as map flow, includes legacy)
        if (!string.IsNullOrWhiteSpace(name))
        {
            var rawName = name.Trim();
            if (rawName.Length < 2)
                return new ReviewResult(false, "Input is too short or empty.", competence.Name);

            var trimmedLower = rawName.ToLowerInvariant();
            if (trimmedLower == "string" || trimmedLower == "null" || trimmedLower == "undefined")
                return new ReviewResult(false, "Invalid input format.", competence.Name);

            var normalizedName = _normalizer.Normalize(rawName);
            if (string.IsNullOrEmpty(normalizedName))
                return new ReviewResult(false, "Input could not be normalized.", competence.Name);

            var duplicateExists = await _db.Competences
                .AnyAsync(c => c.Normalized == normalizedName && c.CompetenceId != competenceId, ct);
            if (duplicateExists)
                return new ReviewResult(false, "Duplicate competence already exists in database.", competence.Name);

            competence.Name = rawName;
            competence.Normalized = normalizedName;
        }

        // Update the competence
        competence.AreaId = areaId;
        competence.CategoryId = categoryId;
        competence.SubcategoryId = subcategoryId;
        // Status remains PendingReview - don't change it

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Competence '{Name}' (ID: {CompetenceId}) categorization updated. AreaId: {AreaId}, CategoryId: {CategoryId}, SubcategoryId: {SubcategoryId}",
            competence.Name, competence.CompetenceId, areaId, categoryId, subcategoryId);

        return new ReviewResult(true, null, competence.Name);
    }

    // Option to assign Competence to Other if not agreeing with LLM mapping
    public async Task<ReviewResult> AssignToOtherAreaAsync(
        Guid competenceId,
        string? reviewNotes,
        CancellationToken ct = default)
    {
        var competence = await _db.Competences
            .FirstOrDefaultAsync(c => c.CompetenceId == competenceId, ct);

        if (competence == null)
            return new ReviewResult(false, $"Competence with ID {competenceId} not found.", null);

        if (competence.Status != CompetenceStatus.PendingReview)
            return new ReviewResult(false,
                $"Competence is not in PendingReview status. Current status: {competence.Status}",
                competence.Name);

        var otherArea = await GetCachedOtherAreaAsync(ct);

        if (otherArea == null)
            return new ReviewResult(false,
                "Area 'Other' was not found in the database.",
                competence.Name);

        // Clear category/subcategory when assigning to "Other" - it's a catch-all area
        competence.AreaId = otherArea.AreaId;
        competence.CategoryId = null;
        competence.SubcategoryId = null;

        competence.Status = CompetenceStatus.Approved;

        competence.ReviewedAt = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(reviewNotes))
        {
            var trimmed = reviewNotes.Trim();
            competence.ReviewNotes = string.IsNullOrWhiteSpace(competence.ReviewNotes)
                ? trimmed
                : $"{competence.ReviewNotes}\n--- {trimmed}";
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Competence '{Name}' (ID: {CompetenceId}) assigned to area 'Other' (AreaId: {AreaId}).",
            competence.Name, competence.CompetenceId, otherArea.AreaId);

        return new ReviewResult(true, null, competence.Name);
    }

    // Bulk mark approved competences as Imported/Completed (e.g. after export to Profiler)
    public async Task<ReviewResult> BulkMarkImportedAsync(
        IEnumerable<Guid> competenceIds,
        CancellationToken ct = default)
    {
        var ids = competenceIds?.Distinct().ToList() ?? new List<Guid>();
        if (ids.Count == 0)
        {
            return new ReviewResult(false, "No competence IDs were provided for bulk import.", null);
        }

        var competences = await _db.Competences
            .Where(c => ids.Contains(c.CompetenceId))
            .ToListAsync(ct);

        if (!competences.Any())
        {
            return new ReviewResult(false, "No competences found for the provided IDs.", null);
        }

        var updated = 0;
        var skipped = 0;

        foreach (var competence in competences)
        {
            if (competence.Status != CompetenceStatus.Approved)
            {
                skipped++;
                continue;
            }

            competence.Status = CompetenceStatus.ImportedCompleted;
            if (!competence.ReviewedAt.HasValue)
            {
                competence.ReviewedAt = DateTime.UtcNow;
            }

            if (string.IsNullOrWhiteSpace(competence.ReviewNotes))
            {
                competence.ReviewNotes = "Imported to Profiler";
            }
            else
            {
                competence.ReviewNotes = $"{competence.ReviewNotes}\n--- Imported to Profiler";
            }

            updated++;
        }

        if (updated == 0)
        {
            return new ReviewResult(false,
                "No competences in Approved status were found to mark as Imported/Completed.",
                null);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Bulk marked {Updated} competences as ImportedCompleted. Skipped (not Approved): {Skipped}.",
            updated, skipped);

        var message = skipped > 0
            ? $"Imported {updated} competences; skipped {skipped} that were not in Approved status."
            : null;

        return new ReviewResult(true, message, null);
    }

    // Bulk move competences back to PendingReview from Approved
    public async Task<ReviewResult> BulkMoveToPendingAsync(
        IEnumerable<Guid> competenceIds,
        CancellationToken ct = default)
    {
        var ids = competenceIds?.Distinct().ToList() ?? new List<Guid>();
        if (ids.Count == 0)
        {
            return new ReviewResult(false, "No competence IDs were provided for bulk move to Pending.", null);
        }

        var competences = await _db.Competences
            .Where(c => ids.Contains(c.CompetenceId))
            .ToListAsync(ct);

        if (!competences.Any())
        {
            return new ReviewResult(false, "No competences found for the provided IDs.", null);
        }

        var updated = 0;
        var skipped = 0;

        foreach (var competence in competences)
        {
            if (competence.Status != CompetenceStatus.Approved)
            {
                skipped++;
                continue;
            }

            competence.Status = CompetenceStatus.PendingReview;
            competence.ReviewedAt = null;
            updated++;
        }

        if (updated == 0)
        {
            return new ReviewResult(false,
                "No competences in Approved status were found to move back to Pending.",
                null);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Bulk moved {Updated} competences from Approved to PendingReview. Skipped (not Approved): {Skipped}.",
            updated, skipped);

        var message = skipped > 0
            ? $"Moved {updated} competences back to Pending; skipped {skipped} that were not in Approved status."
            : null;

        return new ReviewResult(true, message, null);
    }

    // Bulk delete competences (Approved only)
    public async Task<ReviewResult> BulkDeleteAsync(
        IEnumerable<Guid> competenceIds,
        CancellationToken ct = default)
    {
        var ids = competenceIds?.Distinct().ToList() ?? new List<Guid>();
        if (ids.Count == 0)
        {
            return new ReviewResult(false, "No competence IDs were provided for bulk delete.", null);
        }

        var competences = await _db.Competences
            .Where(c => ids.Contains(c.CompetenceId))
            .ToListAsync(ct);

        if (!competences.Any())
        {
            return new ReviewResult(false, "No competences found for the provided IDs.", null);
        }

        var toDelete = competences
            .Where(c => c.Status == CompetenceStatus.Approved)
            .ToList();

        var skipped = competences.Count - toDelete.Count;

        if (!toDelete.Any())
        {
            return new ReviewResult(false,
                "No competences in Approved status were found to delete.",
                null);
        }

        _db.Competences.RemoveRange(toDelete);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Bulk deleted {Deleted} competences in Approved status. Skipped (not Approved): {Skipped}.",
            toDelete.Count, skipped);

        var message = skipped > 0
            ? $"Deleted {toDelete.Count} competences; skipped {skipped} that were not in Approved status."
            : null;

        return new ReviewResult(true, message, null);
    }

    /// <summary>
    /// Deletes an approved competence from the review queue.
    /// </summary>
    /// <remarks>
    /// This intentionally mirrors <see cref="BulkDeleteAsync"/>. Pending, rejected,
    /// legacy, and already-imported rows are kept so reviewers cannot accidentally
    /// remove source catalogue/history records from the single-delete endpoint.
    /// </remarks>
    public async Task<ReviewResult> DeleteAsync(
        Guid competenceId,
        CancellationToken ct = default)
    {
        var competence = await _db.Competences
            .FirstOrDefaultAsync(c => c.CompetenceId == competenceId, ct);

        if (competence == null)
        {
            return new ReviewResult(false, $"Competence with ID {competenceId} not found.", null);
        }

        var name = competence.Name;

        if (competence.Status != CompetenceStatus.Approved)
        {
            return new ReviewResult(false,
                $"Only competences in Approved status can be deleted. Current status: {competence.Status}",
                name);
        }

        _db.Competences.Remove(competence);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Competence '{Name}' (ID: {CompetenceId}) deleted from database.",
            name, competenceId);

        return new ReviewResult(true, null, name);
    }

}
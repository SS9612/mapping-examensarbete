using Mapping_LIA.Entities;

namespace Mapping_LIA.Services.Review;

public interface IReviewService
{
    Task<IEnumerable<CompetenceListItem>> GetPendingReviewAsync(int skip, int take, CancellationToken ct = default);
    Task<CompetenceDetail?> GetByIdAsync(Guid competenceId, CancellationToken ct = default);
    Task<ReviewResult> ApproveAsync(Guid competenceId, string? reviewNotes, CancellationToken ct = default);
    Task<ReviewResult> RejectAsync(Guid competenceId, string reviewNotes, CancellationToken ct = default);
    Task<ReviewResult> AssignToOtherAreaAsync(Guid competenceId, string? reviewNotes, CancellationToken ct = default);
    Task<IEnumerable<CompetenceListItem>> GetApprovedAsync(int skip, int take, CancellationToken ct = default);
    Task<IEnumerable<CompetenceListItem>> GetRejectedAsync(int skip, int take, CancellationToken ct = default);
    Task<IEnumerable<CompetenceListItem>> GetLegacyImportedAsync(int skip, int take, CancellationToken ct = default);
    Task<IEnumerable<CompetenceListItem>> GetImportedCompletedAsync(int skip, int take, CancellationToken ct = default);
    Task<IEnumerable<CompetenceListItem>> GetArchiveAsync(int skip, int take, CancellationToken ct = default);
    Task<ReviewResult> BulkApproveAsync(IEnumerable<Guid> competenceIds, string? reviewNotes, CancellationToken ct = default);
    Task<ReviewResult> BulkMarkImportedAsync(IEnumerable<Guid> competenceIds, CancellationToken ct = default);
    Task<ReviewResult> BulkRejectAsync(IEnumerable<Guid> competenceIds, string reviewNotes, CancellationToken ct = default);
    Task<ReviewResult> BulkMoveToPendingAsync(IEnumerable<Guid> competenceIds, CancellationToken ct = default);
    Task<ReviewResult> BulkDeleteAsync(IEnumerable<Guid> competenceIds, CancellationToken ct = default);
    Task<MetadataResult> GetMetadataAsync(CancellationToken ct = default);
    Task<ReviewResult> UpdateCategorizationAsync(Guid competenceId, Guid areaId, Guid? categoryId, Guid? subcategoryId, string? name = null, CancellationToken ct = default);
    Task<ReviewCounts> GetCountsAsync(CancellationToken ct = default);
    Task<ReviewResult> DeleteAsync(Guid competenceId, CancellationToken ct = default);
}
// Records for review service
public record CompetenceListItem(
    Guid CompetenceId,
    string Name,
    string Normalized,
    string? AreaName,
    string? CategoryName,
    string? SubcategoryName,
    double Confidence,
    string? MatchedType,
    DateTime CreatedAt,
    DateTime? ReviewedAt,
    string? ReviewNotes
);
// Record for competence detail
public record CompetenceDetail(
    Guid CompetenceId,
    string Name,
    string Normalized,
    Guid? AreaId,
    string? AreaName,
    Guid? CategoryId,
    string? CategoryName,
    Guid? SubcategoryId,
    string? SubcategoryName,
    CompetenceStatus Status,
    double Confidence,
    string? MatchedType,
    DateTime CreatedAt,
    DateTime? ReviewedAt,
    string? ReviewNotes
);
// Record for review result
public record ReviewResult(bool Success, string? ErrorMessage, string? CompetenceName);

// Record for metadata (areas, categories, subcategories)
public record MetadataResult(
    IEnumerable<AreaMetadata> Areas,
    IEnumerable<CategoryMetadata> Categories,
    IEnumerable<SubcategoryMetadata> Subcategories
);

public record AreaMetadata(Guid AreaId, string Name);
public record CategoryMetadata(Guid CategoryId, string Name, Guid AreaId);
public record SubcategoryMetadata(Guid SubcategoryId, string Name, Guid CategoryId);

// Record for aggregate counts per status for dashboard KPIs
public record ReviewCounts(
    int Pending,
    int Approved,
    int Rejected,
    int Legacy,
    int Imported,
    int Archive
);
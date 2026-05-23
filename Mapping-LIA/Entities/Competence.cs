using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Mapping_LIA.Entities;

/// <summary>
/// Stored competence candidate or catalogue entry.
/// </summary>
/// <remarks>
/// The same table holds newly mapped review items, seeded legacy catalogue rows,
/// and approved items that have already been imported to Profiler. Always check
/// <see cref="Status"/> before deciding which UI/action should show the row.
/// </remarks>
[Table("Competences")]
public class Competence
{
    [Key]
    public Guid CompetenceId { get; set; }

    [Required]
    [MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string Normalized { get; set; } = string.Empty;

    public Guid? AreaId { get; set; }

    [ForeignKey("AreaId")]
    public virtual Area? Area { get; set; }

    public Guid? CategoryId { get; set; }

    [ForeignKey("CategoryId")]
    public virtual Category? Category { get; set; }

    public Guid? SubcategoryId { get; set; }

    [ForeignKey("SubcategoryId")]
    public virtual Subcategory? Subcategory { get; set; }

    [Required]
    public CompetenceStatus Status { get; set; } = CompetenceStatus.PendingReview;

    [Required]
    public double Confidence { get; set; }

    [MaxLength(50)]
    public string? MatchedType { get; set; }

    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ReviewedAt { get; set; }

    [MaxLength(1000)]
    public string? ReviewNotes { get; set; }


}

/// <summary>
/// Lifecycle state for a competence as it moves from LLM mapping to human review and Profiler import.
/// </summary>
public enum CompetenceStatus
{
    /// <summary>New LLM-mapped item waiting for human review.</summary>
    PendingReview = 0,

    /// <summary>Human-approved item that can be exported/imported to Profiler.</summary>
    Approved = 1,

    /// <summary>Human-rejected item kept for audit/review context.</summary>
    Rejected = 2,

    /// <summary>Existing catalogue item seeded or transferred from the legacy import source.</summary>
    LegacyImported = 3,

    /// <summary>Approved item that has been marked as completed/imported to Profiler.</summary>
    ImportedCompleted = 4
}

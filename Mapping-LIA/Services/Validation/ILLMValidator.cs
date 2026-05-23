namespace Mapping_LIA.Services.Validation;

public interface ILLMValidator
{
    Task<ValidationResult> ValidateInputAsync(string input, IEnumerable<string> referenceTerms, CancellationToken ct);
    Task<ValidationResult> VerifyMatchAsync(string input, string matchedArea, string? matchedCategory, string? matchedSubcategory, string matchedType, CancellationToken ct);
    Task<ValidationResult> ValidateAndVerifyAsync(string input, IEnumerable<string> referenceTerms, string matchedArea, string? matchedCategory, string? matchedSubcategory, string matchedType, double llmConfidence, CancellationToken ct);
    Task<SemanticMatchResult?> FindSemanticMatchAsync(string input, IEnumerable<SemanticMatchCandidate> candidates, CancellationToken ct);
    Task<IReadOnlyList<CompetenceCleanupResult>> CleanupCompetencesAsync(IEnumerable<string> inputs, CancellationToken ct);
}

public record SemanticMatchCandidate(string Type, string Name, string? AreaName = null, string? CategoryName = null);
public record SemanticMatchResult(int CandidateIndex, double Confidence, string Reasoning, int SuggestedCategoryIndex = -1, int SuggestedSubcategoryIndex = -1);

public record ValidationResult(bool IsValid, string? ErrorMessage, double? Confidence = null, string? Reasoning = null);

public record CompetenceCleanupResult(int Index, bool Keep, string Canonical);

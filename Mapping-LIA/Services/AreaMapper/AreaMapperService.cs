using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mapping_LIA.Data;
using Mapping_LIA.Entities;
using Mapping_LIA.Services.Normalization;
using Mapping_LIA.Services.Validation;

namespace Mapping_LIA.Services.AreaMapper;

/// <summary>
/// Maps raw competence names to the seeded area/category/subcategory taxonomy.
/// </summary>
/// <remarks>
/// The service combines deterministic normalization and duplicate checks with
/// LLM semantic matching. Saved results intentionally land in PendingReview so a
/// human can validate the category before import to Profiler.
/// </remarks>
public class AreaMapperService : IAreaMapperService
{
    private readonly ApplicationDbContext _db;
    private readonly ITextNormalizer _normalizer;
    private readonly ILLMValidator _llmValidator;
    private readonly ILogger<AreaMapperService> _logger;

    // Reused across calls
    public record AbbreviationInfo(
        string FullTerm,
        string Category,
        string Description,
        string ExampleUsage
    );

    private static readonly Dictionary<string, AbbreviationInfo> AbbreviationMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cms"] = new(
                FullTerm: "Content Management System",
                Category: "Software System",
                Description: "Software used to create, manage, and publish digital content, usually for websites.",
                ExampleUsage: "We built the company website on a headless CMS."
            ),
            ["dms"] = new(
                FullTerm: "Document Management System",
                Category: "Software System",
                Description: "System for storing, organizing, and tracking electronic documents and files.",
                ExampleUsage: "All contracts are stored in our DMS for version control."
            ),
            ["qms"] = new(
                FullTerm: "Quality Management System",
                Category: "Process / Framework",
                Description: "Framework of processes and tools for ensuring and improving product or service quality.",
                ExampleUsage: "The QMS is aligned with ISO 9001 standards."
            ),
            ["pm"] = new(
                FullTerm: "Project Management",
                Category: "Discipline",
                Description: "Discipline of planning, organizing, and overseeing work to achieve project goals.",
                ExampleUsage: "We use agile project management in our development team."
            ),
            ["bi"] = new(
                FullTerm: "Business Intelligence",
                Category: "Analytics",
                Description: "Technologies and practices for collecting and analyzing data to support decisions.",
                ExampleUsage: "The BI dashboards show sales performance in real time."
            ),
        };

    // Cached reference terms (areas, categories, subcategories). These are process-local
    // because the taxonomy is seeded and rarely changes during normal review work.
    private static List<string>? _cachedReferenceTerms;
    private static readonly SemaphoreSlim _referenceTermsSemaphore = new(1, 1);
    private static DateTime _referenceTermsCacheTime = DateTime.MinValue;
    private static readonly TimeSpan ReferenceTermsCacheExpiry = TimeSpan.FromMinutes(30);

    // Cached approved competences for similarity matching
    private static List<string>? _cachedApprovedCompetences;
    private static readonly SemaphoreSlim _approvedCompetencesSemaphore = new(1, 1);
    private static DateTime _approvedCompetencesCacheTime = DateTime.MinValue;
    private static readonly TimeSpan ApprovedCompetencesCacheExpiry = TimeSpan.FromMinutes(30);

    // Cached candidate data 
    private static CandidateCacheData? _cachedCandidateData;
    private static readonly SemaphoreSlim _candidateDataSemaphore = new(1, 1);
    private static DateTime _candidateDataCacheTime = DateTime.MinValue;
    private static readonly TimeSpan CandidateDataCacheExpiry = TimeSpan.FromMinutes(30);

    private record CandidateCacheData(
        List<SemanticMatchCandidate> Candidates,
        List<(Guid areaId, string areaName, Guid? categoryId, Guid? subcategoryId, string type, string name)> CandidateData
    );

    public AreaMapperService(
        ApplicationDbContext db,
        ITextNormalizer normalizer,
        ILLMValidator llmValidator,
        ILogger<AreaMapperService> logger)
    {
        _db = db;
        _normalizer = normalizer;
        _llmValidator = llmValidator;
        _logger = logger;
    }

    public async Task<MapResult> MapCompetenceAsync(string competence, CancellationToken ct = default)
    {
        if (competence is null)
            return new MapResult(false, "Provide 'competence'.", null);

        var rawInput = competence.Trim();

        if (string.IsNullOrWhiteSpace(rawInput))
            return new MapResult(false, "Input is too short or empty.", null);

        if (rawInput.Length < 2)
            return new MapResult(false, "Input is too short or empty.", null);

        var trimmedInput = rawInput.ToLowerInvariant();
        if (trimmedInput == "string" || trimmedInput == "null" || trimmedInput == "undefined" || trimmedInput.Length < 2)
            return new MapResult(false, "Invalid input format.", null);

        var expanded = ExpandAbbreviations(rawInput);
        var normalized = _normalizer.Normalize(expanded);

        if (string.IsNullOrEmpty(normalized))
            return new MapResult(false, "Input could not be normalized.", null);

        var duplicate = await _db.Competences
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Normalized == normalized, ct);

        if (duplicate != null)
            return new MapResult(false, "Duplicate competence already exists in database.", null);

        MatchResult matchResult;
        try
        {
            matchResult = await FindBestMatchAsync(normalized, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to find match for competence '{Input}'. Semantic matching encountered an error.",
                rawInput);

            return new MapResult(
                false,
                "An error occurred during matching. Please try again or contact support if the issue persists.",
                null);
        }

        // Reject matches with low LLM confidence (< 0.2) or no area match - threshold chosen to filter out noise
        if (matchResult.Score < 0.2 || matchResult.AreaId == Guid.Empty)
            return new MapResult(false, "No matching area/category/subcategory found. Must match at least one to be saved.", null);

        var matchedCategoryName = matchResult.CategoryName;
        var matchedSubcategoryName = matchResult.SubcategoryName;

        // LLM-based validation
        var referenceTerms = await GetSmartReferenceTermsAsync(rawInput, ct);
        var llmValidation = await _llmValidator.ValidateAndVerifyAsync(
            rawInput,
            referenceTerms,
            matchResult.AreaName,
            matchedCategoryName,
            matchedSubcategoryName,
            matchResult.MatchedType,
            matchResult.Score,
            ct);

        if (!llmValidation.IsValid)
            return new MapResult(
                false,
                llmValidation.ErrorMessage ??
                "Validation failed: input may be misspelled, non-English, or the match is incorrect.",
                null);

        // Prefer the validator's confidence because it sees both the original input
        // and proposed hierarchy. Fall back to semantic-match confidence if the
        // validator omitted it.
        var finalConfidence = llmValidation.Confidence ?? matchResult.Score;

        // Use LLM reasoning when present; otherwise note that validation had no reasoning (e.g. empty/truncated response)
        var reviewNotes = !string.IsNullOrWhiteSpace(llmValidation.Reasoning)
            ? llmValidation.Reasoning
            : "LLM validation returned no reasoning (empty or truncated response). Match accepted for review.";

        // Save to database 
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var competenceEntity = new Competence
            {
                CompetenceId = Guid.NewGuid(),
                Name = rawInput,
                Normalized = normalized,
                AreaId = matchResult.AreaId,
                CategoryId = matchResult.CategoryId,
                SubcategoryId = matchResult.SubcategoryId,
                Status = CompetenceStatus.PendingReview,
                Confidence = Math.Round(finalConfidence, 4),
                MatchedType = matchResult.MatchedType,
                CreatedAt = DateTime.UtcNow,
                ReviewNotes = reviewNotes
            };

            _db.Competences.Add(competenceEntity);
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            // Try to rollback, but don't let rollback failure hide the original error
            try
            {
                await transaction.RollbackAsync(ct);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogError(
                    rollbackEx,
                    "Failed to rollback transaction after save failure for competence '{Input}'. Original error preserved.",
                    rawInput);
            }

            _logger.LogError(
                ex,
                "Failed to save competence '{Input}' after successful LLM matching. LLM quota was consumed but data was not saved.",
                rawInput);

            throw;
        }

        _logger.LogInformation(
            "Competence saved for review: '{Input}' matched {Type} '{MatchedItem}' in Area '{AreaName}' with final confidence {FinalConfidence}",
            rawInput, matchResult.MatchedType, matchResult.MatchedItem, matchResult.AreaName, finalConfidence);

        var response = new MapResponse(
            rawInput,
            normalized,
            matchResult.AreaName,
            finalConfidence);

        return new MapResult(true, null, response);
    }

    // Batch mapping method that processes multiple competences
    public async Task<BatchMapResult> MapCompetencesAsync(IEnumerable<string> competences, CancellationToken ct = default)
    {
        if (competences == null)
        {
            _logger.LogWarning("Batch mapping received a null competence collection.");
            return new BatchMapResult(Array.Empty<MapResponse>(), new List<string> { "Provide at least one competence." });
        }

        var distinctInputs = competences
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!distinctInputs.Any())
            return new BatchMapResult(new List<MapResponse>(), new List<string> { "Provide at least one competence." });

        var results = new List<MapResponse>();
        var errors = new List<string>();

        // Process each competence individually
        foreach (var input in distinctInputs)
        {
            var result = await MapCompetenceAsync(input, ct);
            if (!result.Success)
            {
                errors.Add($"{input}: {result.ErrorMessage}");
            }
            else if (result.Response != null)
            {
                results.Add(result.Response);
            }
        }

        return new BatchMapResult(results, errors);
    }

    // Helper record to hold match results
    private record MatchResult(
        Guid AreaId,
        string AreaName,
        Guid? CategoryId,
        Guid? SubcategoryId,
        string? CategoryName,
        string? SubcategoryName,
        double Score,
        string MatchedType,
        string MatchedItem
    );

    private async Task<MatchResult> FindBestMatchAsync(string normalized, CancellationToken ct)
    {
        // Check if we have any areas by using cached candidate data
        var cacheData = await GetCachedCandidateDataAsync(ct);
        if (!cacheData.Candidates.Any(c => c.Type == "Area"))
            return new MatchResult(Guid.Empty, string.Empty, null, null, null, null, 0.0, string.Empty, string.Empty);

        _logger.LogInformation(
            "Attempting LLM semantic matching for: {Input}",
            normalized);

        var semanticMatch = await TrySemanticMatchingAsync(normalized, ct);

        if (semanticMatch.HasValue)
        {
            _logger.LogInformation(
                "LLM semantic match found: {Type} '{Item}' in Area '{AreaName}' with score {Score}",
                semanticMatch.Value.matchedType, semanticMatch.Value.matchedItem, semanticMatch.Value.areaName, semanticMatch.Value.score);

            return new MatchResult(
                semanticMatch.Value.areaId,
                semanticMatch.Value.areaName,
                semanticMatch.Value.categoryId,
                semanticMatch.Value.subcategoryId,
                semanticMatch.Value.categoryName,
                semanticMatch.Value.subcategoryName,
                semanticMatch.Value.score,
                semanticMatch.Value.matchedType,
                semanticMatch.Value.matchedItem
            );
        }

        _logger.LogWarning(
            "No LLM semantic match found for: {Input}",
            normalized);

        return new MatchResult(Guid.Empty, string.Empty, null, null, null, null, 0.0, string.Empty, string.Empty);
    }

    private async Task<List<string>> GetCachedReferenceTermsAsync(CancellationToken ct)
    {
        if (_cachedReferenceTerms != null &&
            DateTime.UtcNow - _referenceTermsCacheTime < ReferenceTermsCacheExpiry)
        {
            return _cachedReferenceTerms;
        }

        // Double-check pattern: another thread might have populated cache while we waited
        await _referenceTermsSemaphore.WaitAsync(ct);
        try
        {
            if (_cachedReferenceTerms != null &&
                DateTime.UtcNow - _referenceTermsCacheTime < ReferenceTermsCacheExpiry)
            {
                return _cachedReferenceTerms;
            }

            // Load from database
            var areas = await _db.Areas.AsNoTracking().Select(a => a.Name).ToListAsync(ct);
            var categories = await _db.Categories.AsNoTracking().Select(c => c.Name).ToListAsync(ct);
            var subcategories = await _db.Subcategories.AsNoTracking().Select(s => s.Name).ToListAsync(ct);

            // Include approved competences for better LLM context
            var approvedCompetences = await _db.Competences
                .AsNoTracking()
                .Where(c => c.Status == CompetenceStatus.Approved)
                .Select(c => c.Name)
                .ToListAsync(ct);

            var referenceTerms = areas
                .Concat(categories)
                .Concat(subcategories)
                .Concat(approvedCompetences)
                .ToList();

            _cachedReferenceTerms = referenceTerms;
            _referenceTermsCacheTime = DateTime.UtcNow;

            return referenceTerms;
        }
        finally
        {
            _referenceTermsSemaphore.Release();
        }
    }

    // Get cached approved competences or load from database
    private async Task<List<string>> GetCachedApprovedCompetencesAsync(CancellationToken ct)
    {
        if (_cachedApprovedCompetences != null &&
            DateTime.UtcNow - _approvedCompetencesCacheTime < ApprovedCompetencesCacheExpiry)
        {
            return _cachedApprovedCompetences;
        }

        await _approvedCompetencesSemaphore.WaitAsync(ct);
        try
        {
            if (_cachedApprovedCompetences != null &&
                DateTime.UtcNow - _approvedCompetencesCacheTime < ApprovedCompetencesCacheExpiry)
            {
                return _cachedApprovedCompetences;
            }

            // Load approved competences from database
            var approvedCompetences = await _db.Competences
                .AsNoTracking()
                .Where(c => c.Status == CompetenceStatus.Approved)
                .Select(c => c.Name)
                .ToListAsync(ct);

            _cachedApprovedCompetences = approvedCompetences;
            _approvedCompetencesCacheTime = DateTime.UtcNow;

            return approvedCompetences;
        }
        finally
        {
            _approvedCompetencesSemaphore.Release();
        }
    }

    // selects most similar approved competences for the input
    private async Task<List<string>> GetSmartReferenceTermsAsync(string input, CancellationToken ct)
    {
        var allReferenceTerms = await GetCachedReferenceTermsAsync(ct);

        var areas = await _db.Areas.AsNoTracking().Select(a => a.Name).ToListAsync(ct);
        var categories = await _db.Categories.AsNoTracking().Select(c => c.Name).ToListAsync(ct);
        var subcategories = await _db.Subcategories.AsNoTracking().Select(s => s.Name).ToListAsync(ct);

        // Get approved competences (cached separately for similarity matching)
        var allApprovedCompetences = await GetCachedApprovedCompetencesAsync(ct);

        // Always include all areas, categories, subcategories
        var smartReferenceTerms = new List<string>();
        smartReferenceTerms.AddRange(areas);
        smartReferenceTerms.AddRange(categories);
        smartReferenceTerms.AddRange(subcategories);

        // Select top 20 most similar approved competences based on input
        if (allApprovedCompetences.Any())
        {
            var similarCompetences = FindMostSimilarCompetences(input, allApprovedCompetences, maxCount: 20);
            smartReferenceTerms.AddRange(similarCompetences);
        }

        return smartReferenceTerms;
    }

    // Find most similar competences using text similarity
    private static List<string> FindMostSimilarCompetences(string input, List<string> candidates, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(input) || !candidates.Any())
            return new List<string>();

        var inputWords = input.ToLowerInvariant()
            .Split(new[] { ' ', '-', '_', '.', ',', '/', '&' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2) // Ignore very short words
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scoredCandidates = candidates
            .Select(candidate =>
            {
                var candidateWords = candidate.ToLowerInvariant()
                    .Split(new[] { ' ', '-', '_', '.', ',', '/', '&' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => w.Length > 2)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var intersection = inputWords.Intersect(candidateWords).Count();
                var union = inputWords.Union(candidateWords).Count();
                var wordOverlapScore = union > 0 ? (double)intersection / union : 0.0;

                var longestCommonSubstring = GetLongestCommonSubstring(
                    input.ToLowerInvariant(),
                    candidate.ToLowerInvariant());
                var substringScore = longestCommonSubstring > 0
                    ? (double)longestCommonSubstring / Math.Max(input.Length, candidate.Length)
                    : 0.0;

                var characterSimilarity = CalculateCharacterSimilarity(
                    input.ToLowerInvariant(),
                    candidate.ToLowerInvariant());

                // Weighted combination: word overlap is most important, then substring, then character similarity
                var finalScore = (wordOverlapScore * 0.5) + (substringScore * 0.3) + (characterSimilarity * 0.2);

                return new { Candidate = candidate, Score = finalScore };
            })
            .Where(x => x.Score > 0.1)
            .OrderByDescending(x => x.Score)
            .Take(maxCount)
            .Select(x => x.Candidate)
            .ToList();

        return scoredCandidates;
    }

    private static int GetLongestCommonSubstring(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            return 0;

        var maxLength = 0;
        for (int i = 0; i < s1.Length; i++)
        {
            for (int j = 0; j < s2.Length; j++)
            {
                var length = 0;
                while (i + length < s1.Length &&
                       j + length < s2.Length &&
                       s1[i + length] == s2[j + length])
                {
                    length++;
                }
                maxLength = Math.Max(maxLength, length);
            }
        }
        return maxLength;
    }

    private static double CalculateCharacterSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
            return 1.0;

        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            return 0.0;

        var maxLen = Math.Max(s1.Length, s2.Length);
        var matches = 0;
        var minLen = Math.Min(s1.Length, s2.Length);

        for (int i = 0; i < minLen; i++)
        {
            if (s1[i] == s2[i])
                matches++;
        }

        if (s1.Contains(s2, StringComparison.OrdinalIgnoreCase) ||
            s2.Contains(s1, StringComparison.OrdinalIgnoreCase))
        {
            matches = Math.Max(matches, minLen);
        }

        return maxLen > 0 ? (double)matches / maxLen : 0.0;
    }

    private async Task<CandidateCacheData> GetCachedCandidateDataAsync(CancellationToken ct)
    {
        if (_cachedCandidateData != null &&
            DateTime.UtcNow - _candidateDataCacheTime < CandidateDataCacheExpiry)
        {
            return _cachedCandidateData;
        }

        await _candidateDataSemaphore.WaitAsync(ct);
        try
        {
            if (_cachedCandidateData != null &&
                DateTime.UtcNow - _candidateDataCacheTime < CandidateDataCacheExpiry)
            {
                return _cachedCandidateData;
            }

            // Load from database
            var candidates = new List<SemanticMatchCandidate>();
            var candidateData = new List<(Guid areaId, string areaName, Guid? categoryId, Guid? subcategoryId, string type, string name)>();

            var areas = await _db.Areas
                .AsNoTracking()
                .Select(a => new { a.AreaId, a.Name })
                .ToListAsync(ct);

            foreach (var a in areas)
            {
                candidates.Add(new SemanticMatchCandidate("Area", a.Name));
                candidateData.Add((a.AreaId, a.Name, null, null, "Area", a.Name));
            }

            var categories = await _db.Categories
                .AsNoTracking()
                .Include(c => c.Area)
                .ToListAsync(ct);

            foreach (var c in categories)
            {
                if (c.Area != null)
                {
                    candidates.Add(new SemanticMatchCandidate("Category", c.Name, c.Area.Name));
                    candidateData.Add((c.Area.AreaId, c.Area.Name, c.CategoryId, null, "Category", c.Name));
                }
            }

            var subcategories = await _db.Subcategories
                .AsNoTracking()
                .Include(s => s.Category)
                .ThenInclude(c => c.Area)
                .ToListAsync(ct);

            foreach (var s in subcategories)
            {
                if (s.Category?.Area != null)
                {
                    candidates.Add(new SemanticMatchCandidate("Subcategory", s.Name, s.Category.Area.Name, s.Category.Name));
                    candidateData.Add((s.Category.Area.AreaId, s.Category.Area.Name, s.CategoryId, s.SubcategoryId, "Subcategory", s.Name));
                }
            }

            var cacheData = new CandidateCacheData(candidates, candidateData);

            _cachedCandidateData = cacheData;
            _candidateDataCacheTime = DateTime.UtcNow;

            return cacheData;
        }
        finally
        {
            _candidateDataSemaphore.Release();
        }
    }

    private async Task<(Guid areaId, string areaName, Guid? categoryId, Guid? subcategoryId, string? categoryName, string? subcategoryName, double score, string matchedType, string matchedItem)?> TrySemanticMatchingAsync(string normalized, CancellationToken ct)
    {
        try
        {
            var cacheData = await GetCachedCandidateDataAsync(ct);

            var candidates = (IReadOnlyList<SemanticMatchCandidate>)cacheData.Candidates;
            var candidateData = (IReadOnlyList<(Guid areaId, string areaName, Guid? categoryId, Guid? subcategoryId, string type, string name)>)cacheData.CandidateData;

            var semanticResult = await _llmValidator.FindSemanticMatchAsync(normalized, candidates, ct);

            if (semanticResult != null &&
                semanticResult.CandidateIndex >= 0 &&
                semanticResult.CandidateIndex < candidateData.Count)
            {
                var match = candidateData[semanticResult.CandidateIndex];

                // Resolve hierarchy: if LLM matched an Area, it also suggests Category/Subcategory to fill the hierarchy
                Guid? finalCategoryId = match.categoryId;
                Guid? finalSubcategoryId = match.subcategoryId;
                string? finalCategoryName = match.type == "Category" ? match.name : null;
                string? finalSubcategoryName = match.type == "Subcategory" ? match.name : null;
                string? suggestedCategoryName = null;
                string? suggestedSubcategoryName = null;

                // If best match is an Area, use suggested Category and Subcategory
                if (match.type == "Area")
                {
                    if (semanticResult.SuggestedCategoryIndex >= 0 &&
                        semanticResult.SuggestedCategoryIndex < candidateData.Count)
                    {
                        var suggestedCategory = candidateData[semanticResult.SuggestedCategoryIndex];
                        if (suggestedCategory.type == "Category" && suggestedCategory.areaId == match.areaId)
                        {
                            finalCategoryId = suggestedCategory.categoryId;
                            finalCategoryName = suggestedCategory.name;
                            suggestedCategoryName = suggestedCategory.name;

                            if (semanticResult.SuggestedSubcategoryIndex >= 0 &&
                                semanticResult.SuggestedSubcategoryIndex < candidateData.Count)
                            {
                                var suggestedSubcategory = candidateData[semanticResult.SuggestedSubcategoryIndex];
                                if (suggestedSubcategory.type == "Subcategory" &&
                                    suggestedSubcategory.categoryId == finalCategoryId)
                                {
                                    finalSubcategoryId = suggestedSubcategory.subcategoryId;
                                    finalSubcategoryName = suggestedSubcategory.name;
                                    suggestedSubcategoryName = suggestedSubcategory.name;
                                }
                            }
                        }
                    }
                }
                else if (match.type == "Category")
                {
                    if (semanticResult.SuggestedSubcategoryIndex >= 0 &&
                        semanticResult.SuggestedSubcategoryIndex < candidateData.Count)
                    {
                        var suggestedSubcategory = candidateData[semanticResult.SuggestedSubcategoryIndex];
                        if (suggestedSubcategory.type == "Subcategory" &&
                            suggestedSubcategory.categoryId == match.categoryId)
                        {
                            finalSubcategoryId = suggestedSubcategory.subcategoryId;
                            finalSubcategoryName = suggestedSubcategory.name;
                            suggestedSubcategoryName = suggestedSubcategory.name;
                        }
                    }
                }
                else if (match.type == "Subcategory")
                {
                    var categoryMatch = candidateData.FirstOrDefault(c =>
                        c.type == "Category" && c.categoryId == match.categoryId);

                    if (!string.IsNullOrEmpty(categoryMatch.name))
                    {
                        finalCategoryName = categoryMatch.name;
                    }
                }

                _logger.LogInformation(
                    "LLM semantic match: {Type} '{Name}' (confidence: {Confidence}, reasoning: {Reasoning})" +
                    (suggestedCategoryName != null ? $", suggested Category: '{suggestedCategoryName}'" : "") +
                    (suggestedSubcategoryName != null ? $", suggested Subcategory: '{suggestedSubcategoryName}'" : ""),
                    match.type, match.name, semanticResult.Confidence, semanticResult.Reasoning);

                return (match.areaId, match.areaName, finalCategoryId, finalSubcategoryId,
                    finalCategoryName, finalSubcategoryName, semanticResult.Confidence, match.type, match.name);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error in semantic matching for input '{Input}': {ErrorMessage}. " +
                "This may indicate an issue with the LLM service, database connection, or data processing.",
                normalized, ex.Message);

            throw new InvalidOperationException(
                $"Semantic matching failed for input '{normalized}'. See inner exception for details.",
                ex);
        }
    }

    private static string ExpandAbbreviations(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var s = input.Replace("\r", "").Replace("\n", "").Trim();

        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (AbbreviationMap.TryGetValue(tokens[i], out var expanded))
            {
                tokens[i] = expanded.FullTerm;
            }
        }

        return string.Join(' ', tokens);
    }
}
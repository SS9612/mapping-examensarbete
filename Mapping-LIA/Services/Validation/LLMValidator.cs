using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Mapping_LIA.Services.Validation;


/// <summary>
/// Azure OpenAI adapter for semantic matching, validation, and cleanup prompts.
/// </summary>
/// <remarks>
/// This service is intentionally conservative around validation failures because
/// accepted results become reviewable competences. Semantic matching can return
/// no match, but validation should not silently approve malformed LLM output.
/// </remarks>
public sealed class LLMValidator : ILLMValidator, IDisposable
{
    // Main service for all LLM validation / matching calls.
    private readonly ILogger<LLMValidator> _logger;
    private readonly HttpClient? _httpClient;
    private readonly string? _apiKey;
    private readonly string _deployment;
    private readonly string _model;
    private readonly string _apiVersion;
    private readonly string _responsesPath;
    private readonly TimeSpan _apiTimeout;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    // Common domain knowledge examples (reused across prompts to reduce duplication)
    private static readonly string[] DomainKnowledgeExamples =
        {
            "- 'Machine Learning' relates to 'Artificial Intelligence'",
            "- 'Agile' and 'Scrum' relate to project management methodologies",
            "- 'CI/CD', 'Version Control', 'Code Review' relate to software development practices",
            "- 'Cloud Computing' relates to infrastructure and platforms",
            "- 'DevOps' relates to software engineering, not civil engineering",
            "- Consider hierarchical relationships (e.g., 'SQL Server Administration' fits under 'Operation, support and infrastructure')"
        };

    // Shared limiter so all calls respect my Azure RPM cap.
    private static readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private const int MaxRequestsPerMinute = 200;
    private static readonly int MinDelayBetweenRequestsMs = (int)Math.Ceiling(60000.0 / MaxRequestsPerMinute);

    private static readonly object _lockObject = new();
    private static DateTime _lastRequestTime = DateTime.MinValue;
    private static int _consecutiveRateLimitErrors;
    private static DateTime _lastRateLimitError = DateTime.MinValue;

    private bool IsConfigured => _httpClient is not null;

    public LLMValidator(IConfiguration configuration, ILogger<LLMValidator> logger)
    {
        _logger = logger;

        _apiKey = configuration["AzureOpenAI:ApiKey"] ??
                  Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

        var endpoint = configuration["AzureOpenAI:Endpoint"] ??
                       Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");

        _deployment = configuration["AzureOpenAI:Deployment"] ??
                      Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ??
                      "gpt-5-mini";

        _model = configuration["AzureOpenAI:Model"] ?? _deployment;
        _apiVersion = configuration["AzureOpenAI:ApiVersion"] ??
                      Environment.GetEnvironmentVariable("AZURE_OPENAI_API_VERSION") ??
                      "2024-12-01-preview";

        var timeoutSeconds = configuration.GetValue<int?>("AzureOpenAI:TimeoutSeconds") ?? 300;
        _apiTimeout = TimeSpan.FromSeconds(timeoutSeconds);

        if (!string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(endpoint))
        {
            var baseUri = endpoint.EndsWith("/", StringComparison.Ordinal)
                ? endpoint
                : $"{endpoint}/";

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseUri, UriKind.Absolute),
                Timeout = Timeout.InfiniteTimeSpan // I handle timeouts via linked cancellation tokens instead
            };

            _httpClient.DefaultRequestHeaders.Add("api-key", _apiKey);
        }
        else
        {
            _logger.LogWarning(
            "Azure OpenAI endpoint or API key missing. LLM validation and semantic matching will be skipped.");
        }

        _responsesPath = $"openai/responses?api-version={_apiVersion}";
    }

    /// <summary>
    /// Uses Azure OpenAI to reject misspellings, non-English terms, and combined
    /// competence names before they enter the review queue.
    /// </summary>
    /// <remarks>
    /// Validation is intentionally fail-closed: if the LLM is unavailable or
    /// returns unreadable JSON, the caller gets a visible error instead of a
    /// silently accepted competence.
    /// </remarks>
    public async Task<ValidationResult> ValidateInputAsync(
        string input,
        IEnumerable<string> referenceTerms,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new ValidationResult(false, "Input cannot be empty.");

        if (!IsConfigured)
            return new ValidationResult(false, "LLM validation is not configured.");

        try
        {
            var referenceTermsList = referenceTerms.ToList();
            var systemPrompt =
                "You are a validation assistant that checks if competence text is valid English and not a misspelling. " +
                "Respond only with JSON: {\"isValid\": true/false, \"errorMessage\": \"reason if invalid\"}";

            var userPrompt = BuildPrompt(input, referenceTermsList);
            var llmResponse = await CallAzureResponsesAsyncCore(systemPrompt, userPrompt, ct, maxOutputTokens: 1024);

            return string.IsNullOrWhiteSpace(llmResponse)
                ? new ValidationResult(false, "LLM validation returned an empty response.")
                : ParseLLMResponse(llmResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Azure OpenAI validation API. Rejecting input as fallback.");
            return new ValidationResult(false, "LLM validation failed. Please retry or review the configuration.");
        }
    }

    /// <summary>
    /// Verifies that the selected area/category/subcategory makes sense for the
    /// proposed competence.
    /// </summary>
    public async Task<ValidationResult> VerifyMatchAsync(
        string input,
        string matchedArea,
        string? matchedCategory,
        string? matchedSubcategory,
        string matchedType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new ValidationResult(false, "Input cannot be empty.");

        if (!IsConfigured)
            return new ValidationResult(false, "LLM match verification is not configured.");

        try
        {
            var systemPrompt =
                "You are a validation assistant that verifies if a competence text correctly matches " +
                "an area/category/subcategory. Respond only with JSON: {\"isValid\": true/false, \"errorMessage\": \"reason if invalid\"}";

            var userPrompt = BuildMatchVerificationPrompt(
                input,
                matchedArea,
                matchedCategory,
                matchedSubcategory,
                matchedType);

            var llmResponse = await CallAzureResponsesAsyncCore(systemPrompt, userPrompt, ct, maxOutputTokens: 1024);

            return string.IsNullOrWhiteSpace(llmResponse)
                ? new ValidationResult(false, "LLM match verification returned an empty response.")
                : ParseLLMResponse(llmResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Azure OpenAI match verification API. Rejecting match as fallback.");
            return new ValidationResult(false, "LLM match verification failed. Please retry or review the configuration.");
        }
    }

    /// <summary>
    /// Runs both input validation and match verification in one LLM request so
    /// the saved review note can explain why the mapping was accepted.
    /// </summary>
    /// <remarks>
    /// The mapping pipeline already used LLM output to choose a candidate, so
    /// accepting malformed validation output would hide the most important
    /// quality signal from reviewers. This method rejects when validation cannot
    /// be interpreted.
    /// </remarks>
    public async Task<ValidationResult> ValidateAndVerifyAsync(
     string input,
     IEnumerable<string> referenceTerms,
     string matchedArea,
     string? matchedCategory,
     string? matchedSubcategory,
     string matchedType,
     double llmConfidence,
     CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new ValidationResult(false, "Input cannot be empty.");

        if (!IsConfigured)
        {
            return new ValidationResult(
                false,
                "LLM validation is not configured.",
                llmConfidence,
                "LLM validation could not run, so the semantic match was not accepted automatically.");
        }

        try
        {
            var systemPrompt =
                "You are a validation assistant. Perform two checks: " +
                "1) Verify the input is valid English and not a misspelling, " +
                "2) Verify the match to the area/category/subcategory is correct and provide your confidence (0.0-1.0) with brief reasoning. " +
                "Respond only with JSON: {\"isValid\": true/false, \"errorMessage\": \"reason if invalid\", \"confidence\": 0.0-1.0, \"reasoning\": \"brief explanation\"}";

            var userPrompt = BuildCombinedPrompt(
                input,
                referenceTerms,
                matchedArea,
                matchedCategory,
                matchedSubcategory,
                matchedType,
                llmConfidence);

            // Retry combined validation if we get empty / truncated / non-JSON output.
            // This prevents "Expected end of string / reached end of data" from interrupting long batches.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                // Allow enough tokens for full JSON + reasoning so we don't get empty/truncated output
                var llmResponse = await CallAzureResponsesAsyncCore(systemPrompt, userPrompt, ct, maxOutputTokens: 1200);

                if (string.IsNullOrWhiteSpace(llmResponse))
                {
                    await Task.Delay(300 * (attempt + 1), ct);
                    continue;
                }

                try
                {
                    return ParseLLMResponseWithConfidence(llmResponse);
                }
                catch (JsonException)
                {
                    // truncated/invalid JSON -> retry
                }

                await Task.Delay(400 * (attempt + 1), ct);
            }

            _logger.LogWarning("Combined validation failed after retries. Rejecting input as fallback.");
            return new ValidationResult(
                false,
                "LLM validation returned empty or malformed output.",
                llmConfidence,
                "LLM validation failed after retries; semantic match was not accepted automatically.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Azure OpenAI combined validation API. Rejecting input as fallback.");
            return new ValidationResult(
                false,
                "LLM validation failed. Please retry or review the configuration.",
                llmConfidence,
                "LLM validation threw an error; semantic match was not accepted automatically.");
        }
    }


    public async Task<SemanticMatchResult?> FindSemanticMatchAsync(
        string input,
        IEnumerable<SemanticMatchCandidate> candidates,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Azure OpenAI not configured. Semantic matching skipped.");
            return null;
        }

        var candidatesList = candidates.ToList();
        if (candidatesList.Count == 0)
            return null;

        try
        {
            var systemPrompt =
                "You are a semantic matching assistant that finds the best logical match for a competence term " +
                "from a list of areas, categories, and subcategories. Consider semantic relationships, domain knowledge, " +
                "and logical connections, not just word overlap. The hierarchy is: Area > Category > Subcategory. " +
                "ALWAYS provide Category and Subcategory suggestions based on semantic fit, even if the best match is only an Area " +
                "or if confidence is low. Respond only with JSON: " +
                "{\"bestMatchIndex\": 0-based index, \"confidence\": 0.0-1.0, \"reasoning\": \"brief explanation\", " +
                "\"suggestedCategoryIndex\": 0-based index or -1, \"suggestedSubcategoryIndex\": 0-based index or -1}";

            var userPrompt = BuildSemanticMatchingPrompt(input, candidatesList);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var llmResponse = await CallAzureResponsesAsyncCore(systemPrompt, userPrompt, ct, maxOutputTokens: 1024);
                if (string.IsNullOrWhiteSpace(llmResponse))
                    continue;

                var parsed = ParseSemanticMatchResponse(llmResponse);
                if (parsed != null)
                    return parsed;

                // parse failed (often truncated JSON) -> retry
                await Task.Delay(300 * (attempt + 1), ct);
            }

            return null;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Azure OpenAI for semantic matching. Returning null.");
            return null;
        }
    }

    // LLM-based cleanup of a batch of competence names.
    public async Task<IReadOnlyList<CompetenceCleanupResult>> CleanupCompetencesAsync(
        IEnumerable<string> inputs,
        CancellationToken ct)
    {
        var list = inputs.ToList();
        if (list.Count == 0)
            return Array.Empty<CompetenceCleanupResult>();

        if (!IsConfigured)
        {
            _logger.LogWarning("Azure OpenAI not configured. Skipping competence cleanup.");
            return list.Select((name, index) => new CompetenceCleanupResult(index, true, name)).ToList();
        }

        try
        {
            var systemPrompt = "You are a competence normalization assistant that cleans and deduplicates skill names.";
            var userPrompt = BuildCompetenceCleanupPrompt(list);
            var llmResponse = await CallAzureResponsesAsyncCore(systemPrompt, userPrompt, ct, maxOutputTokens: 1200);

            return string.IsNullOrWhiteSpace(llmResponse)
                ? list.Select((name, index) => new CompetenceCleanupResult(index, true, name)).ToList()
                : ParseCleanupResponse(llmResponse, list);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during competence cleanup. Returning original list.");
            return list.Select((name, index) => new CompetenceCleanupResult(index, true, name)).ToList();
        }
    }

    // Core Azure OpenAI HTTP call with retry / throttling logic.
    private async Task<string?> CallAzureResponsesAsyncCore(
        string? systemPrompt,
            string userPrompt,
        CancellationToken ct,
        int maxOutputTokens)
    {
        if (_httpClient is null)
            return null;

        var payload = new AzureResponseRequest(
     Model: _model,
     Input: BuildInputMessages(systemPrompt, userPrompt),
     MaxOutputTokens: maxOutputTokens,
     Text: new AzureTextConfig(new AzureTextFormat("json_object"))
 );
        const int maxRetries = 1;   // 2 attempts total – fail fast on empty/output errors to speed up mapping
        const int baseDelayMs = 500;

        await _rateLimiter.WaitAsync(ct); // serialize calls so rate-limit bookkeeping is consistent
        try
        {
            await HandleRateLimitBackoff(ct);
            await ThrottleRequestAsync(ct);

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // Use linked token so I can apply my own timeout while still respecting external cancellation
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(_apiTimeout);

                    var request = new HttpRequestMessage(HttpMethod.Post, _responsesPath)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(payload, _serializerOptions),
                            Encoding.UTF8,
                            "application/json")
                    };

                    _logger.LogInformation(
                        "Calling Azure OpenAI deployment {Deployment}. Attempt {Attempt}/{Total}",
                        _deployment, attempt + 1, maxRetries + 1);

                    var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutCts.Token);

                    await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
                    using var reader = new StreamReader(stream);
                    var rawBody = await reader.ReadToEndAsync();


                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        // Track consecutive 429s for adaptive backoff
                        lock (_lockObject)
                        {
                            _consecutiveRateLimitErrors++;
                            _lastRateLimitError = DateTime.UtcNow;
                        }

                        if (attempt < maxRetries)
                        {
                            var delay = baseDelayMs * (int)Math.Pow(2, attempt); // Exponential backoff
                            _logger.LogWarning(
                            "Azure OpenAI rate limited (429). Retrying in {Delay}ms (attempt {Attempt}/{Total}).",
                            delay, attempt + 1, maxRetries + 1);
                            await Task.Delay(delay, ct);
                            continue;
                        }

                        _logger.LogWarning(
                        "Azure OpenAI rate limited after {MaxRetries} retries. Skipping request.",
                            maxRetries);
                        return null;
                    }

                    if ((int)response.StatusCode >= 500)
                    {
                        if (attempt < maxRetries)
                        {
                            var delay = baseDelayMs * (int)Math.Pow(2, attempt);
                            _logger.LogWarning(
                                "Azure OpenAI server error {Status}. Retrying in {Delay}ms.",
                                response.StatusCode, delay);
                            await Task.Delay(delay, ct);
                            continue;
                        }

                        _logger.LogError(
                            "Azure OpenAI server error {Status}. Response: {Body}",
                            response.StatusCode, rawBody);
                        return null;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning(
                            "Azure OpenAI request failed with status {Status}. Body: {Body}",
                            response.StatusCode, rawBody);
                        return null;
                    }

                    // Reset rate limit tracking on successful request
                    lock (_lockObject)
                    {
                        _consecutiveRateLimitErrors = 0;
                        _lastRequestTime = DateTime.UtcNow;
                    }

                    var envelope = JsonSerializer.Deserialize<AzureResponseEnvelope>(rawBody, _serializerOptions);

                    if (string.Equals(envelope?.Status, "incomplete", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Azure OpenAI returned incomplete response. Reason: {Reason}", envelope?.IncompleteDetails?.Reason);

                        if (attempt < maxRetries)
                        {
                            var delay = baseDelayMs * (int)Math.Pow(2, attempt);
                            await Task.Delay(delay, ct);
                            continue;
                        }

                        return null;
                    }
                    var text = ExtractOutputText(envelope);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        if (attempt < maxRetries)
                        {
                            var delay = baseDelayMs * (int)Math.Pow(2, attempt);
                            _logger.LogWarning("Azure OpenAI returned empty output_text. Retrying in {Delay}ms.", delay);
                            await Task.Delay(delay, ct);
                            continue;
                        }

                        _logger.LogWarning("Azure OpenAI returned empty output_text after retries.");
                        return null;
                    }

                    return text;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // Don't retry if user cancelled
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    var delay = baseDelayMs * (int)Math.Pow(2, attempt);
                    _logger.LogWarning(
            ex,
            "Azure OpenAI request failed. Retrying in {Delay}ms (attempt {Attempt}/{Total}).",
            delay, attempt + 1, maxRetries + 1);
                    await Task.Delay(delay, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Azure OpenAI request failed.");
                    return null;
                }
            }
        }
        finally
        {
            _rateLimiter.Release();
        }

        _logger.LogWarning("Azure OpenAI request exhausted all retries.");
        return null;
    }

    private static IReadOnlyList<AzureInputMessage> BuildInputMessages(string? systemPrompt, string userPrompt)
    {
        var messages = new List<AzureInputMessage>();

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new AzureInputMessage(
    "system",
    new[] { new AzureInputContent("input_text", systemPrompt!) }));
        }

        messages.Add(new AzureInputMessage(
            "user",
            new[] { new AzureInputContent("input_text", userPrompt) }));

        return messages;
    }

    // Helper to pull the first "output_text" chunk from the responses API.
    private static string? ExtractOutputText(AzureResponseEnvelope? envelope)
    {
        var parts = envelope?.Output?
            .SelectMany(o => o.Content ?? Array.Empty<AzureResponseContent>())
            .Where(c => c.Type == "output_text" && !string.IsNullOrEmpty(c.Text))
            .Select(c => c.Text)
            .ToList();

        if (parts == null || parts.Count == 0)
            return null;

        return string.Concat(parts);
    }

    private SemanticMatchResult? ParseSemanticMatchResponse(string messageContent)
    {
        var jsonContent = ExtractJsonFromResponse(messageContent);

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            if (!root.TryGetProperty("bestMatchIndex", out var indexProp))
                return null;

            var index = indexProp.GetInt32();
            if (index < 0)
                return null;

            var confidence = root.TryGetProperty("confidence", out var confProp)
                ? confProp.GetDouble()
                : 0.5; // Default to medium confidence if missing

            var reasoning = root.TryGetProperty("reasoning", out var reasonProp)
                ? reasonProp.GetString()
                : "Semantic match found";

            var suggestedCategoryIndex = root.TryGetProperty("suggestedCategoryIndex", out var catIndexProp)
                ? catIndexProp.GetInt32()
                : -1;

            var suggestedSubcategoryIndex = root.TryGetProperty("suggestedSubcategoryIndex", out var subcatIndexProp)
                ? subcatIndexProp.GetInt32()
                : -1;

            return new SemanticMatchResult(
                index,
                confidence,
                reasoning ?? "Semantic match found",
                suggestedCategoryIndex,
                suggestedSubcategoryIndex);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse semantic match response as JSON: {Response}", messageContent);
            return null;
        }
    }

    // Parse cleanup JSON, but always fall back to original list if anything looks wrong.
    private IReadOnlyList<CompetenceCleanupResult> ParseCleanupResponse(string messageContent, IReadOnlyList<string> original)
    {
        var json = ExtractJsonFromResponse(messageContent);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("results", out var resultsProp) ||
                resultsProp.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("Cleanup response missing 'results' array. Response: {Response}", messageContent);
                return original.Select((name, index) => new CompetenceCleanupResult(index, true, name)).ToList();
            }

            var defaults = original
                .Select((name, index) => new CompetenceCleanupResult(index, true, name))
                .ToArray();

            foreach (var elem in resultsProp.EnumerateArray())
            {
                if (!elem.TryGetProperty("index", out var indexProp))
                    continue;

                var index = indexProp.GetInt32();
                if (index < 0 || index >= original.Count)
                    continue;

                var keep = elem.TryGetProperty("keep", out var keepProp)
                    ? keepProp.GetBoolean()
                    : true;

                var canonical = elem.TryGetProperty("canonical", out var canonProp)
                    ? canonProp.GetString() ?? original[index]
                    : original[index];

                defaults[index] = new CompetenceCleanupResult(index, keep, canonical);
            }

            // Safety check: if LLM says to delete everything, ignore it and keep originals
            if (defaults.All(r => !r.Keep))
            {
                return original.Select((name, index) => new CompetenceCleanupResult(index, true, name)).ToList();
            }

            return defaults;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse cleanup response. Response: {Response}", messageContent);
            return original.Select((name, index) => new CompetenceCleanupResult(index, true, name)).ToList();
        }
    }

    // Simple delay between requests based on last send time.
    private async Task ThrottleRequestAsync(CancellationToken ct)
    {
        int delayMs = 0;

        lock (_lockObject)
        {
            var timeSinceLastRequest = DateTime.UtcNow - _lastRequestTime;
            if (timeSinceLastRequest.TotalMilliseconds < MinDelayBetweenRequestsMs)
            {
                delayMs = MinDelayBetweenRequestsMs - (int)timeSinceLastRequest.TotalMilliseconds;
            }
        }

        if (delayMs > 0)
        {
            _logger.LogDebug("Throttling Azure OpenAI request by {Delay}ms to honor RPM cap.", delayMs);
            await Task.Delay(delayMs, ct);
        }
    }

    // Extra backoff when I see multiple 429s in a short window.
    private async Task HandleRateLimitBackoff(CancellationToken ct)
    {
        int backoffMs = 0;
        int errorCount = 0;

        lock (_lockObject)
        {
            if (_consecutiveRateLimitErrors > 0)
            {
                var timeSinceLastError = DateTime.UtcNow - _lastRateLimitError;
                // Only apply backoff if errors were recent (within 60s), otherwise reset counter
                if (timeSinceLastError.TotalSeconds < 60)
                {
                    var backoffSeconds = Math.Min(Math.Pow(2, _consecutiveRateLimitErrors - 1), 30); // Exponential, capped at 30s
                    backoffMs = (int)(backoffSeconds * 1000);
                    errorCount = _consecutiveRateLimitErrors;
                }
                else
                {
                    _consecutiveRateLimitErrors = 0; // Errors too old, reset
                }
            }
        }

        if (backoffMs > 0)
        {
            _logger.LogInformation(
                "Applying rate-limit backoff: waiting {Delay}ms due to {ErrorCount} recent 429 responses.",
                backoffMs,
                errorCount);
            await Task.Delay(backoffMs, ct);
        }
    }

    private string BuildSemanticMatchingPrompt(string input, List<SemanticMatchCandidate> candidates)
    {
        var estimatedCapacity = 500 + (candidates.Count * 50) + 800;
        var sb = new StringBuilder(estimatedCapacity);
        sb.AppendLine("Find the best semantic match for the following competence:");
        sb.AppendLine($"Competence: \"{input}\"");
        sb.AppendLine();
        sb.AppendLine("Candidates (consider semantic relationships, domain knowledge, and logical connections):");

        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            sb.Append($"{i}. [{c.Type}] {c.Name}");
            if (!string.IsNullOrEmpty(c.AreaName))
                sb.Append($" (Area: {c.AreaName})");
            if (!string.IsNullOrEmpty(c.CategoryName))
                sb.Append($" (Category: {c.CategoryName})");
            sb.AppendLine();
        }

        sb.AppendLine();
        sb.AppendLine("Hierarchy Structure:");
        sb.AppendLine("- Areas are top-level (e.g., 'Software Engineering', 'Project Management')");
        sb.AppendLine("- Categories belong to Areas (e.g., 'Web Development' under 'Software Engineering')");
        sb.AppendLine("- Subcategories belong to Categories (e.g., 'Frontend Development' under 'Web Development')");
        sb.AppendLine();
        sb.AppendLine("Instructions:");
        sb.AppendLine("- Consider semantic meaning, not just word overlap");
        sb.AppendLine("- Think about domain knowledge and hierarchical relationships");
        foreach (var example in DomainKnowledgeExamples)
        {
            sb.AppendLine(example);
        }
        sb.AppendLine();
        sb.AppendLine("IMPORTANT - Always Provide Suggestions:");
        sb.AppendLine("- ALWAYS provide suggestedCategoryIndex and suggestedSubcategoryIndex based on semantic fit");
        sb.AppendLine("- If bestMatchIndex is an Area: suggest the best Category and Subcategory within that Area");
        sb.AppendLine("- If bestMatchIndex is a Category: suggest the best Subcategory within that Category (set suggestedCategoryIndex to bestMatchIndex)");
        sb.AppendLine("- If bestMatchIndex is a Subcategory: set both suggestedCategoryIndex and suggestedSubcategoryIndex to bestMatchIndex");
        sb.AppendLine("- Even if confidence is low (< 0.3), still provide best-guess suggestions based on semantic similarity");
        sb.AppendLine("- If truly no suitable Category/Subcategory exists, use -1 for that index");
        sb.AppendLine("- Suggestions should be semantically related, not just the first item in the hierarchy");
        sb.AppendLine();
        sb.AppendLine("Respond with JSON only: {\"bestMatchIndex\": number, \"confidence\": 0.0-1.0, \"reasoning\": \"explanation\", \"suggestedCategoryIndex\": number or -1, \"suggestedSubcategoryIndex\": number or -1}");
        sb.AppendLine("If no good match exists (confidence < 0.3), set bestMatchIndex to -1, but still provide suggestions if possible.");

        return sb.ToString();
    }

    private string BuildPrompt(string input, List<string> referenceTerms)
    {
        var estimatedCapacity = 400 + input.Length + (Math.Min(referenceTerms.Count, 20) * 30);
        var sb = new StringBuilder(estimatedCapacity);
        sb.AppendLine("Validate the following competence text:");
        sb.AppendLine($"Input: \"{input}\"");
        sb.AppendLine();
        sb.AppendLine("Check if:");
        sb.AppendLine("1. The text contains only valid English words (technical terms are allowed)");
        sb.AppendLine("2. The text is NOT a misspelling of any existing term");
        sb.AppendLine();

        if (referenceTerms.Any())
        {
            sb.AppendLine("Reference terms to check against (for misspelling detection):");
            foreach (var term in referenceTerms.Take(20)) // Limit to 20 to avoid huge prompts
                sb.AppendLine($"- {term}");
            sb.AppendLine();
        }

        sb.AppendLine("Respond with JSON only: {\"isValid\": true/false, \"errorMessage\": \"reason if invalid\"}");
        sb.AppendLine("Examples:");
        sb.AppendLine("- Valid: \"Project Management\", \"Software Development\", \"C# Programming\", \".NET Blazor\", \"ASP.NET Core\"");
        sb.AppendLine("- Invalid (misspelling): \"Projekt Management\" (should be \"Project\")");
        sb.AppendLine("- Invalid (non-English): \"Projektledning\" (Swedish)");
        sb.AppendLine("- Invalid (combination of separate competences): \".NET C#\" (should be two separate competences: \".NET\" and \"C#\")");
        sb.AppendLine("- Invalid (combination): \"Python Java\" (should be two separate competences)");
        sb.AppendLine("- Invalid (combination): \"JavaScript TypeScript\" (should be two separate competences)");
        sb.AppendLine("Note: A competence containing multiple words is valid if it represents a single technology/framework (e.g., \".NET Blazor\", \"ASP.NET\").");
        sb.AppendLine("But reject if it's clearly combining two separate competences that should be listed separately.");

        return sb.ToString();
    }

    private string BuildMatchVerificationPrompt(
        string input,
        string matchedArea,
        string? matchedCategory,
        string? matchedSubcategory,
        string matchedType)
    {
        var estimatedCapacity = 600 + input.Length + matchedArea.Length +
            (matchedCategory?.Length ?? 0) + (matchedSubcategory?.Length ?? 0) + matchedType.Length;
        var sb = new StringBuilder(estimatedCapacity);
        sb.AppendLine("Verify if the following competence text correctly matches the assigned area/category/subcategory:");
        sb.AppendLine();
        sb.AppendLine($"Competence: \"{input}\"");
        sb.AppendLine($"Matched Area: \"{matchedArea}\"");

        if (!string.IsNullOrEmpty(matchedCategory))
            sb.AppendLine($"Matched Category: \"{matchedCategory}\"");

        if (!string.IsNullOrEmpty(matchedSubcategory))
            sb.AppendLine($"Matched Subcategory: \"{matchedSubcategory}\"");

        sb.AppendLine($"Match Type: {matchedType}");
        sb.AppendLine();
        sb.AppendLine("Check if:");
        sb.AppendLine("1. The competence text is semantically related to the matched area/category/subcategory");
        sb.AppendLine("2. The match makes logical sense");
        sb.AppendLine("3. The competence would reasonably belong in this area/category/subcategory");
        sb.AppendLine();
        sb.AppendLine("Respond with JSON only: {\"isValid\": true/false, \"errorMessage\": \"reason if invalid\"}");
        sb.AppendLine("Examples:");
        sb.AppendLine("- Valid: \"Project Management\" matched to \"IS/IT and Communication\" -> Project Management & Management category");
        sb.AppendLine("- Invalid: \"Cooking\" matched to \"Software and Electrical Engineering\" -> No logical connection");

        return sb.ToString();
    }

    private string BuildCompetenceCleanupPrompt(IReadOnlyList<string> inputs)
    {
        var estimatedCapacity = 800 + (inputs.Count * 35);
        var sb = new StringBuilder(estimatedCapacity);
        sb.AppendLine("You are cleaning up a list of competence terms.");
        sb.AppendLine("Goal: remove duplicates, near-duplicates and over-specific variants when a more general parent competence exists.");
        sb.AppendLine();
        sb.AppendLine("Very important rules:");
        sb.AppendLine("- Group technologies that are essentially the same skill.");
        sb.AppendLine("- If a general technology exists (like \".NET\"), treat framework names and version numbers as variants of it.");
        sb.AppendLine("- Prefer base platform + base language over specific frameworks/versions.");
        sb.AppendLine("- Only keep a more specific item if it represents a clearly different competence (e.g. \"Java\" vs \"JavaScript\").");
        sb.AppendLine();
        sb.AppendLine("Example:");
        sb.AppendLine("Input competences:");
        sb.AppendLine("0: .NET");
        sb.AppendLine("1: .NET Blazor");
        sb.AppendLine("2: .Net C#");
        sb.AppendLine("3: .NET Core");
        sb.AppendLine("4: .NET Maui");
        sb.AppendLine("5: .NET5");
        sb.AppendLine();
        sb.AppendLine("Correct behaviour for the example:");
        sb.AppendLine("- All items 0,1,3,4,5 are variations of \".NET\" (frameworks, versions, platforms).");
        sb.AppendLine("- Item 2 (\".Net C#\") is the programming language C# and should be treated as \"C#\".");
        sb.AppendLine("- Final kept competences: \".NET\" and \"C#\".");
        sb.AppendLine();
        sb.AppendLine("Output format (JSON only):");
        sb.AppendLine("{");
        sb.AppendLine("  \"results\": [");
        sb.AppendLine("    { \"index\": number, \"keep\": true/false, \"canonical\": \"canonical name\" },");
        sb.AppendLine("    ... one object per input competence, in the same order ...");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Now clean up this list of competences:");

        for (int i = 0; i < inputs.Count; i++)
            sb.AppendLine($"{i}: {inputs[i]}");

        return sb.ToString();
    }

    private string BuildCombinedPrompt(
        string input,
        IEnumerable<string> referenceTerms,
        string matchedArea,
        string? matchedCategory,
        string? matchedSubcategory,
        string matchedType,
        double llmConfidence)
    {
        var referenceTermsList = referenceTerms as IList<string> ?? referenceTerms.ToList();

        var estimatedCapacity = 1000 + input.Length + matchedArea.Length +
            (matchedCategory?.Length ?? 0) + (matchedSubcategory?.Length ?? 0) + matchedType.Length +
            (Math.Min(referenceTermsList.Count, 20) * 30);
        var sb = new StringBuilder(estimatedCapacity);
        sb.AppendLine("Perform two validation checks on the following competence:");
        sb.AppendLine();
        sb.AppendLine($"Competence: \"{input}\"");
        sb.AppendLine();
        sb.AppendLine("CHECK 1 - Input Validation:");
        sb.AppendLine("1. Is the text valid English? (technical terms are allowed)");
        sb.AppendLine("2. Is it NOT a misspelling of any existing term?");
        sb.AppendLine();

        if (referenceTermsList.Any())
        {
            sb.AppendLine("Reference terms to check against (for misspelling detection):");
            foreach (var term in referenceTermsList.Take(20)) // Limit to 20 to avoid huge prompts
                sb.AppendLine($"- {term}");
            sb.AppendLine();
        }

        sb.AppendLine("CHECK 2 - Match Verification and Confidence Assessment:");
        sb.AppendLine($"Matched Area: \"{matchedArea}\"");
        if (!string.IsNullOrEmpty(matchedCategory))
            sb.AppendLine($"Matched Category: \"{matchedCategory}\"");
        if (!string.IsNullOrEmpty(matchedSubcategory))
            sb.AppendLine($"Matched Subcategory: \"{matchedSubcategory}\"");
        sb.AppendLine($"Match Type: {matchedType}");
        sb.AppendLine($"LLM Confidence Score: {llmConfidence:F4} (from semantic matching)");
        sb.AppendLine();
        sb.AppendLine("Tasks:");
        sb.AppendLine("1. Verify the competence is semantically related to the matched area/category/subcategory");
        sb.AppendLine("2. Verify the match makes logical sense - be lenient and consider domain knowledge");
        sb.AppendLine("3. Consider the LLM confidence score, but provide your own confidence assessment (0.0-1.0)");
        sb.AppendLine("4. Provide brief reasoning (1-2 sentences) for your confidence level");
        sb.AppendLine();
        sb.AppendLine("Important guidelines:");
        sb.AppendLine("- Be lenient: if a competence is semantically related (even loosely), consider it valid");
        sb.AppendLine("- REJECT competences that combine multiple separate competences (e.g., \".NET C#\" should be two separate competences)");
        sb.AppendLine("- ACCEPT competences that are single technologies/frameworks with multiple words (e.g., \".NET Blazor\", \"ASP.NET Core\", \"C# Programming\")");
        sb.AppendLine("Examples of INVALID (should be rejected/split):");
        sb.AppendLine("  - \".NET C#\" (two separate competences)");
        sb.AppendLine("  - \"Python Java\" (two separate competences)");
        sb.AppendLine("  - \"JavaScript TypeScript\" (two separate competences)");
        sb.AppendLine("Examples of VALID (single competence):");
        sb.AppendLine("  - \".NET Blazor\" (single technology/framework)");
        sb.AppendLine("  - \"ASP.NET Core\" (single technology/framework)");
        sb.AppendLine("  - \"C# Programming\" (single competence describing C#)");
        sb.AppendLine("  - \"React Native\" (single framework)");
        foreach (var example in DomainKnowledgeExamples)
        {
            sb.AppendLine(example);
        }
        sb.AppendLine("- Only reject if the match is clearly wrong (e.g., 'Cooking' matched to 'Software Engineering')");
        sb.AppendLine();
        sb.AppendLine("Respond with JSON only: {\"isValid\": true/false, \"errorMessage\": \"reason if invalid\", \"confidence\": 0.0-1.0, \"reasoning\": \"brief explanation\"}");
        sb.AppendLine("Do NOT add any text before or after this JSON object. Do NOT include markdown, code fences, or commentary.");
        sb.AppendLine("The input is invalid if EITHER check fails. If valid, always provide confidence and reasoning as a single, complete sentence in the 'reasoning' field.");

        return sb.ToString();
    }

    // Tries to strip markdown/code fences and grab the JSON object from the LLM response.
    private string ExtractJsonFromResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        ReadOnlySpan<char> span = response.AsSpan().Trim();

        if (span.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            span = span[7..];
        }
        else if (span.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            span = span[3..];
        }

        span = span.Trim();

        if (span.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^3];
        }

        span = span.Trim();

        var startIndex = span.IndexOf('{');
        if (startIndex < 0)
            return span.ToString();

        var endIndex = span.LastIndexOf('}');

        // Edge case: if closing brace is before opening brace (malformed JSON), try to salvage partial JSON
        if (endIndex < startIndex)
        {
            var lastComma = span.LastIndexOf(',');
            var lastQuote = span.LastIndexOf('"');

            if (lastQuote > startIndex)
            {
                var potentialEnd = Math.Max(lastComma, lastQuote);
                if (potentialEnd > startIndex)
                {
                    var partial = span[startIndex..].ToString();
                    return partial;
                }
            }
        }

        if (startIndex >= 0 && endIndex > startIndex)
        {
            return span[startIndex..(endIndex + 1)].ToString();
        }

        return span[startIndex..].ToString();
    }

    // Parse the simple { isValid, errorMessage } result. On failure, reject so the problem is visible to reviewers.
    private ValidationResult ParseLLMResponse(string messageContent)
    {
        var jsonContent = ExtractJsonFromResponse(messageContent);

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            var isValid = root.GetProperty("isValid").GetBoolean();
            var errorMessage = root.TryGetProperty("errorMessage", out var errorProp)
                ? errorProp.GetString()
                : null;

            return new ValidationResult(isValid, errorMessage);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse LLM response as JSON: {Response}. Rejecting input.", messageContent);
            return new ValidationResult(false, "LLM validation returned malformed JSON.");
        }
    }

    // Parse the extended result with confidence + reasoning. If JSON is bad, I try to salvage isValid/confidence.
    private ValidationResult ParseLLMResponseWithConfidence(string messageContent)
    {
        var jsonContent = ExtractJsonFromResponse(messageContent);

        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            var root = doc.RootElement;

            var isValid = root.GetProperty("isValid").GetBoolean();
            var errorMessage = root.TryGetProperty("errorMessage", out var errorProp)
                ? errorProp.GetString()
                : null;

            double? confidence = null;
            if (root.TryGetProperty("confidence", out var confidenceProp))
            {
                if (confidenceProp.ValueKind == JsonValueKind.Number)
                {
                    confidence = confidenceProp.GetDouble();
                }
                else if (double.TryParse(confidenceProp.GetString(), out var confValue))
                {
                    confidence = confValue;
                }
            }

            string? reasoning = null;
            if (root.TryGetProperty("reasoning", out var reasoningProp))
            {
                reasoning = reasoningProp.GetString();
            }

            return new ValidationResult(isValid, errorMessage, confidence, reasoning);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to parse LLM response as JSON. Response length: {Length}, Response: {Response}. Rejecting input.",
                messageContent?.Length ?? 0,
                messageContent);

            // Last resort: try regex extraction if JSON parsing completely failed
            if (!string.IsNullOrWhiteSpace(messageContent))
            {
                try
                {
                    var isValidMatch = System.Text.RegularExpressions.Regex.Match(messageContent, @"""isValid""\s*:\s*(true|false)");
                    var confidenceMatch = System.Text.RegularExpressions.Regex.Match(messageContent, @"""confidence""\s*:\s*([0-9.]+)");

                    if (isValidMatch.Success && bool.TryParse(isValidMatch.Groups[1].Value, out var isValid))
                    {
                        double? confidence = null;
                        if (confidenceMatch.Success && double.TryParse(confidenceMatch.Groups[1].Value, out var confValue))
                        {
                            confidence = confValue;
                        }

                        // We managed to salvage isValid/confidence from a malformed JSON response.
                        // Surface that fact in Reasoning so reviewers understand why the note looks generic.
                        var reasoning = "LLM returned malformed JSON; salvaged validity/confidence from partial response.";
                        return new ValidationResult(isValid, null, confidence, reasoning);
                    }
                }
                catch
                {
                }
            }

            // Complete failure to parse or salvage anything useful. Reject so the caller can retry.
            var fallbackReasoning = "LLM response was malformed or empty; semantic match was not accepted automatically.";
            return new ValidationResult(false, "LLM validation returned malformed JSON.", null, fallbackReasoning);
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    private sealed record AzureResponseRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] IReadOnlyList<AzureInputMessage> Input,
    [property: JsonPropertyName("max_output_tokens")] int MaxOutputTokens,
    [property: JsonPropertyName("text")] AzureTextConfig? Text = null
);

    private sealed record AzureTextConfig(
        [property: JsonPropertyName("format")] AzureTextFormat Format
    );

    private sealed record AzureTextFormat(
        [property: JsonPropertyName("type")] string Type // "text" | "json_object" | "json_schema"
    );

    private sealed record AzureInputMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] IReadOnlyList<AzureInputContent> Content);

    private sealed record AzureInputContent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string Text);

    private sealed record AzureResponseEnvelope(
      [property: JsonPropertyName("output")] IReadOnlyList<AzureResponseOutput>? Output,
      [property: JsonPropertyName("status")] string? Status = null,
      [property: JsonPropertyName("incomplete_details")] AzureIncompleteDetails? IncompleteDetails = null);

    private sealed record AzureIncompleteDetails(
        [property: JsonPropertyName("reason")] string? Reason = null);
    private sealed record AzureResponseOutput(
        [property: JsonPropertyName("content")] IReadOnlyList<AzureResponseContent>? Content);

    private sealed record AzureResponseContent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}

namespace Mapping_LIA.Services.Validation;

public class LLMValidatorOptions
{
    // Enable caching of validation results to reduce LLM API calls
    public bool EnableCaching { get; set; } = true;

    // Cache expiration time in minutes
    public int CacheExpirationMinutes { get; set; } = 60 * 24; // 24 hours

    // Maximum number of reference terms to include in prompt (to reduce token usage)
    public int MaxReferenceTerms { get; set; } = 20;

    // Timeout for LLM API calls in seconds
    public int TimeoutSeconds { get; set; } = 30;

    // Retry count for failed LLM API calls
    public int RetryCount { get; set; } = 2;
}

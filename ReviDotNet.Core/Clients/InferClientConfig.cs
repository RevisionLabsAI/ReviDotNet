namespace Revi;

public class InferClientConfig
{
    public string ApiUrl { get; set; }
    public string ApiKey { get; set; }
    public bool UseApiKey { get; set; }
    public Protocol Protocol { get; set; }
    public string DefaultModel { get; set; }
    public int TimeoutSeconds { get; set; }
    public int DelayBetweenRequestsMs { get; set; }
    public int RetryAttemptLimit { get; set; }
    public int RetryInitialDelaySeconds { get; set; }
    public int SimultaneousRequests { get; set; }
    public bool SupportsCompletion { get; set; }
    public bool SupportsGuidance { get; set; }
    public GuidanceType? DefaultGuidanceType { get; set; }
    public string? DefaultGuidanceString { get; set; }
    // Inactivity timeout for non-responsive providers (seconds)
    public int InactivityTimeoutSeconds { get; set; } = 60;
}
namespace Revi;

/// <summary>
/// One observed outcome of an inference call against a provider, as reported to
/// <see cref="InferenceProviderMonitor"/>.
/// </summary>
/// <param name="ProviderName">The provider profile's name (for example "openai", "groq"), lowercased by the monitor.</param>
/// <param name="ModelName">The model the call named, when known.</param>
/// <param name="Succeeded">Whether the call succeeded.</param>
/// <param name="Failure">The classified failure; meaningless when <paramref name="Succeeded"/> is true.</param>
/// <param name="Streaming">Whether the call was a streaming one.</param>
public readonly record struct InferenceProviderOutcome(
    string ProviderName,
    string? ModelName,
    bool Succeeded,
    InferenceFailure Failure,
    bool Streaming);

/// <summary>
/// The seam between ReviDotNet and its host for provider health. The library classifies failures
/// and refuses to retry the hopeless ones; it deliberately does not decide what to do about a
/// provider that cannot pay — taking it out of rotation, alerting someone, showing it on a
/// dashboard are all product decisions that belong to the host.
/// </summary>
/// <remarks>
/// <para>
/// A static event rather than an injected observer because the HTTP clients are constructed deep
/// inside <see cref="ProviderProfile"/> from configuration, with no container in reach. Handlers
/// are invoked on the calling thread and must return promptly; a handler that throws is swallowed,
/// because an inference call must never fail because a health listener did.
/// </para>
/// </remarks>
public static class InferenceProviderMonitor
{
    /// <summary>
    /// Raised after every classified inference outcome. Subscribers must not block or throw.
    /// </summary>
    public static event Action<InferenceProviderOutcome>? OutcomeObserved;

    /// <summary>Reports a successful call, which clears a provider's failure run in the host.</summary>
    /// <param name="providerName">The provider profile name.</param>
    /// <param name="modelName">The model that answered, when known.</param>
    /// <param name="streaming">Whether the call was streaming.</param>
    public static void ReportSuccess(string? providerName, string? modelName, bool streaming)
        => Report(new InferenceProviderOutcome(
            Normalize(providerName), modelName, Succeeded: true, default, streaming));

    /// <summary>Reports a classified failure.</summary>
    /// <param name="providerName">The provider profile name.</param>
    /// <param name="modelName">The model the call named, when known.</param>
    /// <param name="failure">The classification.</param>
    /// <param name="streaming">Whether the call was streaming.</param>
    public static void ReportFailure(string? providerName, string? modelName, InferenceFailure failure, bool streaming)
        => Report(new InferenceProviderOutcome(
            Normalize(providerName), modelName, Succeeded: false, failure, streaming));

    /// <summary>Delivers an outcome to every subscriber, isolating the caller from all of them.</summary>
    /// <param name="outcome">The outcome.</param>
    private static void Report(InferenceProviderOutcome outcome)
    {
        Action<InferenceProviderOutcome>? handlers = OutcomeObserved;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<InferenceProviderOutcome>)handler)(outcome);
            }
            catch (Exception ex)
            {
                Util.Log($"InferenceProviderMonitor: a health subscriber threw and was ignored: {ex.Message}");
            }
        }
    }

    /// <summary>Lowercases a provider name and tolerates a missing one.</summary>
    /// <param name="providerName">The raw name.</param>
    /// <returns>The normalized name, or "unknown".</returns>
    private static string Normalize(string? providerName)
        => string.IsNullOrWhiteSpace(providerName) ? "unknown" : providerName.Trim().ToLowerInvariant();
}

/// <summary>
/// Thrown when an inference call fails in a way that retrying cannot fix. Carries the
/// classification so a caller can tell "the account cannot pay" from "that model is gone" without
/// parsing the message.
/// </summary>
public sealed class InferenceProviderException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="providerName">The provider profile name.</param>
    /// <param name="failure">The classification.</param>
    /// <param name="message">The full message, including the provider's own body.</param>
    public InferenceProviderException(string providerName, InferenceFailure failure, string message)
        : base(message)
    {
        ProviderName = providerName;
        Failure = failure;
    }

    /// <summary>The provider that failed.</summary>
    public string ProviderName { get; }

    /// <summary>The classification.</summary>
    public InferenceFailure Failure { get; }

    /// <summary>The failure kind, for callers that only branch on it.</summary>
    public InferenceFailureKind Kind => Failure.Kind;
}

// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Translates an adapter-neutral routing policy into provider-specific request parameters.
/// </summary>
internal interface IProviderRoutingAdapter
{
    /// <summary>Adds native routing fields to an outgoing request payload.</summary>
    /// <param name="payload">The request payload to augment.</param>
    void Apply(Dictionary<string, object> payload);
}

/// <summary>
/// Selects and validates the routing adapter associated with a provider protocol.
/// </summary>
internal static class ProviderRoutingAdapterFactory
{
    /// <summary>Creates an adapter for an explicitly configured routing policy.</summary>
    /// <param name="config">The inference-client configuration.</param>
    /// <returns>The adapter, or <see langword="null"/> when no routing setting is configured.</returns>
    /// <exception cref="NotSupportedException">
    /// Routing settings were configured for a protocol without a routing adapter.
    /// </exception>
    internal static IProviderRoutingAdapter? Create(InferClientConfig config)
    {
        ProviderRoutingPolicy? policy = config.RoutingPolicy;
        if (policy is null || !policy.HasConfiguredSettings)
            return null;

        policy.Validate();
        return config.Protocol switch
        {
            Protocol.OpenRouter => new OpenRouterRoutingAdapter(policy, config.ApiUrl),
            _ => throw new NotSupportedException(
                $"Routing settings are configured, but protocol '{config.Protocol}' has no routing adapter.")
        };
    }
}

/// <summary>
/// Converts the common routing policy into OpenRouter's nested <c>provider</c> request object.
/// </summary>
internal sealed class OpenRouterRoutingAdapter : IProviderRoutingAdapter
{
    /// <summary>The validated, adapter-neutral routing policy.</summary>
    private readonly ProviderRoutingPolicy _policy;

    /// <summary>Initializes and validates an OpenRouter routing adapter.</summary>
    /// <param name="policy">The common routing policy to translate.</param>
    /// <param name="apiUrl">The configured OpenRouter API base URL.</param>
    /// <exception cref="InvalidOperationException">
    /// The configured data-residency boundary cannot be enforced by the API URL.
    /// </exception>
    internal OpenRouterRoutingAdapter(ProviderRoutingPolicy policy, string apiUrl)
    {
        _policy = policy;
        ValidateDataResidency(policy.DataResidency, apiUrl);
    }

    /// <inheritdoc/>
    public void Apply(Dictionary<string, object> payload)
    {
        Dictionary<string, object> provider = [];

        AddList(provider, "order", _policy.UpstreamOrder);
        AddList(provider, "only", _policy.AllowedUpstreams);
        AddList(provider, "ignore", _policy.BlockedUpstreams);
        AddList(provider, "quantizations", _policy.AllowedQuantizations);

        if (_policy.AllowFallbacks.HasValue)
            provider["allow_fallbacks"] = _policy.AllowFallbacks.Value;
        if (_policy.RequireRequestParameterSupport.HasValue)
            provider["require_parameters"] = _policy.RequireRequestParameterSupport.Value;
        if (_policy.SelectionStrategy.HasValue)
            provider["sort"] = ToOpenRouterSort(_policy.SelectionStrategy.Value);
        if (_policy.PreferredMinimumThroughputTokensPerSecond.HasValue)
            provider["preferred_min_throughput"] = _policy.PreferredMinimumThroughputTokensPerSecond.Value;
        if (_policy.PreferredMaximumLatencySeconds.HasValue)
            provider["preferred_max_latency"] = _policy.PreferredMaximumLatencySeconds.Value;
        if (_policy.DataRetention == DataRetentionPolicy.None)
            provider["zdr"] = true;
        if (_policy.DataCollection.HasValue)
            provider["data_collection"] = _policy.DataCollection.Value.ToString().ToLowerInvariant();

        Dictionary<string, object> maximumPrice = [];
        if (_policy.MaximumCostPerMillionInputTokens.HasValue)
            maximumPrice["prompt"] = _policy.MaximumCostPerMillionInputTokens.Value;
        if (_policy.MaximumCostPerMillionOutputTokens.HasValue)
            maximumPrice["completion"] = _policy.MaximumCostPerMillionOutputTokens.Value;
        if (maximumPrice.Count > 0)
            provider["max_price"] = maximumPrice;

        if (provider.Count > 0)
            payload["provider"] = provider;
    }

    /// <summary>Adds a normalized string list when at least one value is configured.</summary>
    /// <param name="target">The native provider-routing object.</param>
    /// <param name="key">The OpenRouter request-field name.</param>
    /// <param name="values">The common routing values.</param>
    private static void AddList(Dictionary<string, object> target, string key, IEnumerable<string>? values)
    {
        List<string> normalized = ProviderRoutingPolicy.NormalizeValues(values);
        if (normalized.Count > 0)
            target[key] = normalized;
    }

    /// <summary>Maps a portable selection strategy to OpenRouter's sort value.</summary>
    /// <param name="strategy">The common selection strategy.</param>
    /// <returns>The OpenRouter sort value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The strategy value is unknown.</exception>
    private static string ToOpenRouterSort(RoutingSelectionStrategy strategy)
    {
        return strategy switch
        {
            RoutingSelectionStrategy.LowestCost => "price",
            RoutingSelectionStrategy.LowestLatency => "latency",
            RoutingSelectionStrategy.HighestThroughput => "throughput",
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown routing selection strategy.")
        };
    }

    /// <summary>Validates whether the OpenRouter base URL enforces the requested data residency.</summary>
    /// <param name="residency">The requested processing boundary.</param>
    /// <param name="apiUrl">The configured OpenRouter API base URL.</param>
    /// <exception cref="InvalidOperationException">The requested boundary is not enforced by the URL.</exception>
    private static void ValidateDataResidency(DataResidencyPolicy? residency, string apiUrl)
    {
        if (!residency.HasValue || residency == DataResidencyPolicy.Any)
            return;

        if (residency == DataResidencyPolicy.EU &&
            Uri.TryCreate(apiUrl, UriKind.Absolute, out Uri? uri) &&
            uri.Host.Equals("eu.openrouter.ai", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (residency == DataResidencyPolicy.EU)
        {
            throw new InvalidOperationException(
                "OpenRouter data-residency = eu requires an https://eu.openrouter.ai API URL and enterprise in-region routing.");
        }

        throw new InvalidOperationException(
            $"OpenRouter does not expose a documented {residency.Value} in-region API endpoint.");
    }
}

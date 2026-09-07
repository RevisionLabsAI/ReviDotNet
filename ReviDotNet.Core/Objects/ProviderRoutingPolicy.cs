// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Describes adapter-neutral constraints and preferences for selecting an inference gateway's
/// upstream provider endpoint.
/// </summary>
public sealed class ProviderRoutingPolicy
{
    /// <summary>Gets or sets the upstreams to try first, in priority order.</summary>
    public List<string>? UpstreamOrder { get; set; }

    /// <summary>Gets or sets the only upstreams that may serve the request.</summary>
    public List<string>? AllowedUpstreams { get; set; }

    /// <summary>Gets or sets upstreams that must not serve the request.</summary>
    public List<string>? BlockedUpstreams { get; set; }

    /// <summary>Gets or sets whether the gateway may try fallback upstreams.</summary>
    public bool? AllowFallbacks { get; set; }

    /// <summary>Gets or sets the attribute used to order eligible upstreams.</summary>
    public RoutingSelectionStrategy? SelectionStrategy { get; set; }

    /// <summary>
    /// Gets or sets whether every routed upstream must support every parameter in the request.
    /// </summary>
    public bool? RequireRequestParameterSupport { get; set; }

    /// <summary>Gets or sets the quantization formats eligible to serve the request.</summary>
    public List<string>? AllowedQuantizations { get; set; }

    /// <summary>Gets or sets the maximum accepted input-token price in dollars per million tokens.</summary>
    public decimal? MaximumCostPerMillionInputTokens { get; set; }

    /// <summary>Gets or sets the maximum accepted output-token price in dollars per million tokens.</summary>
    public decimal? MaximumCostPerMillionOutputTokens { get; set; }

    /// <summary>Gets or sets the preferred minimum generation throughput in tokens per second.</summary>
    public double? PreferredMinimumThroughputTokensPerSecond { get; set; }

    /// <summary>Gets or sets the preferred maximum observed latency in seconds.</summary>
    public double? PreferredMaximumLatencySeconds { get; set; }

    /// <summary>Gets or sets the required upstream prompt and response retention policy.</summary>
    public DataRetentionPolicy? DataRetention { get; set; }

    /// <summary>Gets or sets whether routed upstreams may collect request data.</summary>
    public DataCollectionPolicy? DataCollection { get; set; }

    /// <summary>Gets or sets the required geographic processing boundary.</summary>
    public DataResidencyPolicy? DataResidency { get; set; }

    /// <summary>Gets whether the policy contains any explicitly configured setting.</summary>
    public bool HasConfiguredSettings =>
        HasValues(UpstreamOrder) ||
        HasValues(AllowedUpstreams) ||
        HasValues(BlockedUpstreams) ||
        AllowFallbacks.HasValue ||
        SelectionStrategy.HasValue ||
        RequireRequestParameterSupport.HasValue ||
        HasValues(AllowedQuantizations) ||
        MaximumCostPerMillionInputTokens.HasValue ||
        MaximumCostPerMillionOutputTokens.HasValue ||
        PreferredMinimumThroughputTokensPerSecond.HasValue ||
        PreferredMaximumLatencySeconds.HasValue ||
        DataRetention.HasValue ||
        DataCollection.HasValue ||
        DataResidency.HasValue;

    /// <summary>
    /// Validates adapter-neutral routing constraints and rejects contradictory or invalid values.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A numeric routing constraint is not positive.</exception>
    /// <exception cref="InvalidOperationException">Upstream allow/block settings contradict each other.</exception>
    public void Validate()
    {
        RequirePositive(MaximumCostPerMillionInputTokens, nameof(MaximumCostPerMillionInputTokens));
        RequirePositive(MaximumCostPerMillionOutputTokens, nameof(MaximumCostPerMillionOutputTokens));
        RequirePositive(PreferredMinimumThroughputTokensPerSecond, nameof(PreferredMinimumThroughputTokensPerSecond));
        RequirePositive(PreferredMaximumLatencySeconds, nameof(PreferredMaximumLatencySeconds));

        HashSet<string> allowed = NormalizeValues(AllowedUpstreams).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> blocked = NormalizeValues(BlockedUpstreams).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] overlap = allowed.Intersect(blocked, StringComparer.OrdinalIgnoreCase).ToArray();
        if (overlap.Length > 0)
        {
            throw new InvalidOperationException(
                $"Routing upstreams cannot be both allowed and blocked: {string.Join(", ", overlap)}.");
        }

        foreach (string upstream in NormalizeValues(UpstreamOrder))
        {
            if (blocked.Contains(upstream))
                throw new InvalidOperationException($"Ordered routing upstream '{upstream}' is also blocked.");
            if (allowed.Count > 0 && !allowed.Contains(upstream))
                throw new InvalidOperationException($"Ordered routing upstream '{upstream}' is not in allowed-upstreams.");
        }
    }

    /// <summary>Returns normalized, non-empty, case-insensitively distinct values.</summary>
    /// <param name="values">The configured values to normalize.</param>
    /// <returns>A normalized list that is safe to serialize.</returns>
    internal static List<string> NormalizeValues(IEnumerable<string>? values)
    {
        if (values is null)
            return [];

        return values
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Determines whether a configured list contains a non-whitespace value.</summary>
    /// <param name="values">The values to inspect.</param>
    /// <returns><see langword="true"/> when at least one usable value exists.</returns>
    private static bool HasValues(IEnumerable<string>? values)
    {
        return values?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;
    }

    /// <summary>Rejects an explicitly configured numeric constraint that is zero or negative.</summary>
    /// <typeparam name="T">The comparable numeric value type.</typeparam>
    /// <param name="value">The nullable value to inspect.</param>
    /// <param name="propertyName">The property name to report when validation fails.</param>
    /// <exception cref="ArgumentOutOfRangeException">The configured value is zero or negative.</exception>
    private static void RequirePositive<T>(T? value, string propertyName) where T : struct, IComparable<T>
    {
        if (value.HasValue && value.Value.CompareTo(default) <= 0)
            throw new ArgumentOutOfRangeException(propertyName, value, "Routing constraints must be greater than zero.");
    }
}

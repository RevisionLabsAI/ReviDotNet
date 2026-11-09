// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Picks the model for a prompt that names none, walking its candidates in the established order —
/// preferred models, then the prompt's tier, then the tier-C fallback — and skipping any whose provider
/// is out of service or excluded.
/// </summary>
/// <remarks>
/// Why it exists: on 2026-11-06 a Gemini prepaid account ran out and every prompt that resolved to a
/// Gemini model kept resolving there, so search submission failed for two days while Claude, OpenAI
/// and Groq sat idle. Selection had no notion of provider health; only one caller (name generation in
/// BetterNamer) skipped an unhealthy provider, and it did so by hand.
/// <para>
/// Candidates are produced lazily, so availability is asked about one candidate at a time and only
/// until one is accepted. With every provider usable the result is identical to the previous
/// selection: the first enabled preferred model, else the first tier model, else the tier-C model.
/// </para>
/// </remarks>
internal static class AutomaticModelSelector
{
    /// <summary>Upper bound on tier candidates examined per tier, so a misbehaving registry cannot loop.</summary>
    private const int MaxTierCandidates = 64;

    /// <summary>
    /// Selects the model for an automatically routed prompt.
    /// </summary>
    /// <param name="promptName">The prompt's name, for log lines.</param>
    /// <param name="preferredModels">The prompt's preferred model names, in order; may be null.</param>
    /// <param name="getModel">Looks a model up by name.</param>
    /// <param name="findTiered">Finds the best enabled model in the prompt's tier, excluding the given names.</param>
    /// <param name="findFallback">Finds the best enabled tier-C model, excluding the given names.</param>
    /// <param name="blockedModels">Models the prompt never allows; may be null.</param>
    /// <param name="isProviderUsable">Whether a provider may be called now; null treats every provider as usable.</param>
    /// <param name="excludedProviders">Providers that must not be chosen at all (one that just failed this call).</param>
    /// <param name="log">Receives a line for every skip and substitution.</param>
    /// <returns>
    /// The first candidate whose provider is usable. When none is usable and nothing was excluded, the
    /// first enabled candidate anyway — trying a provider that may have recovered beats refusing to run.
    /// Null when there is no candidate, or every candidate is on an excluded provider.
    /// </returns>
    public static ModelProfile? Select(
        string? promptName,
        IReadOnlyList<string>? preferredModels,
        Func<string, ModelProfile?> getModel,
        Func<List<string>, ModelProfile?> findTiered,
        Func<List<string>, ModelProfile?> findFallback,
        IReadOnlyList<string>? blockedModels,
        Func<string, bool>? isProviderUsable,
        IReadOnlyCollection<string>? excludedProviders,
        Action<string> log)
    {
        ModelProfile? firstUnavailable = null;
        bool haveExclusions = excludedProviders is { Count: > 0 };

        foreach (ModelProfile candidate in Candidates(promptName, preferredModels, getModel, findTiered, findFallback, blockedModels, log))
        {
            string provider = ProviderOf(candidate);

            if (haveExclusions && excludedProviders!.Contains(provider, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (isProviderUsable is null || string.IsNullOrEmpty(provider) || isProviderUsable(provider))
            {
                if (firstUnavailable is not null)
                {
                    log($"FindModel: prompt '{promptName}' routed to '{candidate.Name}' on provider '{provider}' " +
                        $"because '{firstUnavailable.Name}' (provider '{ProviderOf(firstUnavailable)}') is out of service.");
                }

                return candidate;
            }

            log($"FindModel: skipping model '{candidate.Name}' for prompt '{promptName}': provider '{provider}' is out of service.");
            firstUnavailable ??= candidate;
        }

        if (firstUnavailable is not null && !haveExclusions)
        {
            log($"FindModel: every candidate provider for prompt '{promptName}' is out of service; " +
                $"using '{firstUnavailable.Name}' anyway rather than refusing the call.");
            return firstUnavailable;
        }

        return null;
    }

    /// <summary>The provider name a model is served by, as the health monitor reports it.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The provider profile name, or the model's declared provider name; empty when neither is set.</returns>
    public static string ProviderOf(ModelProfile model)
        => model.Provider?.Name ?? model.ProviderName ?? string.Empty;

    /// <summary>
    /// Yields candidate models in selection order, each at most once: enabled preferred models, then
    /// the prompt's tier, then the tier-C fallback. Tier models are produced by asking the registry
    /// again with each already-offered model blocked, so a later candidate is only computed when an
    /// earlier one was refused.
    /// </summary>
    private static IEnumerable<ModelProfile> Candidates(
        string? promptName,
        IReadOnlyList<string>? preferredModels,
        Func<string, ModelProfile?> getModel,
        Func<List<string>, ModelProfile?> findTiered,
        Func<List<string>, ModelProfile?> findFallback,
        IReadOnlyList<string>? blockedModels,
        Action<string> log)
    {
        HashSet<string> offered = new(StringComparer.Ordinal);
        bool hadPreferences = preferredModels is { Count: > 0 };

        if (hadPreferences)
        {
            foreach (string preferredName in preferredModels!)
            {
                ModelProfile? preferred = getModel(preferredName);
                if (preferred is not null && preferred.Enabled)
                {
                    if (offered.Add(preferred.Name))
                        yield return preferred;
                    continue;
                }

                // A preferred model that isn't registered (or is disabled) silently changes which model
                // actually runs — and with it both output quality and cost. Log every skip so a
                // substitution is a visible, deliberate fact instead of a surprise in the usage report.
                log($"FindModel: preferred model '{preferredName}' for prompt '{promptName}' " +
                    $"is {(preferred is null ? "not registered" : "disabled")}; trying next preference/tier fallback.");
            }
        }

        bool loggedTierFallback = false;
        foreach (ModelProfile tiered in FromTier(findTiered, blockedModels, offered))
        {
            if (hadPreferences && !loggedTierFallback)
            {
                log($"FindModel: prompt '{promptName}' fell back to tier model '{tiered.Name}' " +
                    "(none of its preferred models was available).");
                loggedTierFallback = true;
            }

            yield return tiered;
        }

        foreach (ModelProfile fallback in FromTier(findFallback, blockedModels, offered))
        {
            yield return fallback;
        }
    }

    /// <summary>Yields a tier's models best-first by re-querying with every offered model blocked.</summary>
    private static IEnumerable<ModelProfile> FromTier(
        Func<List<string>, ModelProfile?> find,
        IReadOnlyList<string>? blockedModels,
        HashSet<string> offered)
    {
        for (int i = 0; i < MaxTierCandidates; i++)
        {
            List<string> blocked = [.. blockedModels ?? [], .. offered];
            ModelProfile? model = find(blocked);
            if (model is null || !model.Enabled || !offered.Add(model.Name))
            {
                yield break;
            }

            yield return model;
        }
    }
}

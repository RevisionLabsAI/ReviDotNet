// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.ComponentModel.DataAnnotations;
using System.Runtime.InteropServices.ObjectiveC;
using Newtonsoft.Json;
using Revi;

namespace Revi;

public class ModelProfile
{
    // ================================
    //  ModelProfile Object Definition
    // ================================
    
    // Identifier
    [RConfigProperty("general_name")]
    public string Name { get; set; } 
    
    [RConfigProperty("general_enabled")]
    public bool Enabled { get; set; } 
    
    
    // Model and Provider
    [RConfigProperty("general_model-string")]
    public string ModelString { get; set; }

    [RConfigProperty("general_provider-name")]
    public string ProviderName { get; set; }
    
    public ProviderProfile Provider { get; set; } 
    
    
    // Overall Options
    [RConfigProperty("settings_tier")]
    public ModelTier Tier { get; set; } 
    
    [RConfigProperty("settings_token-limit")]
    public int TokenLimit { get; set; } 
    
    [RConfigProperty("settings_stop-sequences")]
    public string? StopSequences { get; set; }
    
    [RConfigProperty("settings_max-token-type")]
    public MaxTokenType? MaxTokenType { get; set; }

    /// <summary>
    /// The model's real maximum OUTPUT tokens per completion — a hardware capability declaration, distinct
    /// from both <see cref="TokenLimit"/> (the context window, which guards INPUT size) and the
    /// <c>[[override-settings]] max-tokens</c> forced override. When set, any requested max-tokens larger
    /// than this is clamped down (with a log line) instead of being rejected by the provider.
    /// </summary>
    [RConfigProperty("settings_max-output-tokens")]
    public int? MaxOutputTokens { get; set; }

    /// <summary>
    /// Named loop-detection algorithm for this model's completions, or null/unset for OFF (the default).
    /// Currently supported: <c>repeat-N</c> (e.g. <c>repeat-512</c>) — flags a completion as a degenerate
    /// repetition loop when its trailing text consists of ≥4 consecutive exact repeats of the same unit
    /// spanning ≥ N characters. Tripping sets <see cref="CompletionResult.FinishReason"/> to
    /// <c>"repetition"</c> (non-streaming) or stops consuming the stream (streaming). Intended for small /
    /// locally-hosted models that can loop until the output ceiling; leave unset for frontier cloud models.
    /// </summary>
    [RConfigProperty("settings_loop-detection")]
    public string? LoopDetection { get; set; }

    /// <summary>
    /// Indicates whether this model supports legacy prompt completion (non-chat) endpoints.
    /// When set, this value overrides any provider-level defaults for prompt completion support.
    /// </summary>
    [RConfigProperty("settings_supports-prompt-completion")]
    public bool? SupportsPromptCompletion { get; set; }

    /// <summary>
    /// The effective prompt-completion capability used by the inference engine: the model-level
    /// <see cref="SupportsPromptCompletion"/> override when set, otherwise the provider's
    /// <c>supports-prompt-completion</c>. This lets a single provider host both completion- and
    /// chat-only models.
    /// </summary>
    public bool EffectiveSupportsPromptCompletion => SupportsPromptCompletion ?? Provider?.SupportsCompletion ?? false;

    /// <summary>
    /// Indicates whether this model supports the newer Responses API completion endpoint.
    /// When set, this value overrides any provider-level defaults for responses completion support.
    /// </summary>
    [RConfigProperty("settings_supports-response-completion")]
    public bool? SupportsResponseCompletion { get; set; }

    /// <summary>
    /// Indicates whether this model accepts image inputs (vision / multimodal). When set, overrides
    /// the provider-level default. Used to select a vision-capable model for the file-reading tools.
    /// </summary>
    [RConfigProperty("settings_supports-vision")]
    public bool? SupportsVision { get; set; }

    /// <summary>
    /// The effective vision capability: the model-level <see cref="SupportsVision"/> override when
    /// set, otherwise the provider's <c>supports-vision</c>, otherwise false.
    /// </summary>
    public bool EffectiveSupportsVision => SupportsVision ?? Provider?.SupportsVision ?? false;

    /// <summary>
    /// USD cost per 1,000,000 prompt/input tokens. Optional — when unset, this model
    /// contributes 0 to cost-budget tracking (useful for free or locally-hosted models).
    /// </summary>
    [RConfigProperty("settings_cost-per-million-input-tokens")]
    public decimal? CostPerMillionInputTokens { get; set; }

    /// <summary>
    /// USD cost per 1,000,000 completion/output tokens. Optional — when unset, this model
    /// contributes 0 to cost-budget tracking.
    /// </summary>
    [RConfigProperty("settings_cost-per-million-output-tokens")]
    public decimal? CostPerMillionOutputTokens { get; set; }

    /// <summary>
    /// Default amount of native thinking / reasoning for this model. Use one of the five <b>common
    /// words</b> — <c>minimal</c>, <c>low</c>, <c>medium</c>, <c>high</c>, <c>max</c> — or <c>none</c>
    /// (also <c>off</c>) to disable, and let the per-model <c>thinking-conversion-*</c> table translate it
    /// to whatever this provider expects. A raw provider value (an effort string or a numeric token
    /// budget) may also be given directly. Every model config should set this default; a prompt's
    /// <c>thinking</c> setting overrides it per request, and a prompt that leaves <c>thinking</c> unset
    /// inherits this model default. The resolved value is emitted in the correct shape per provider:
    /// Claude adaptive effort or classic <c>budget_tokens</c>; Gemini <c>thinkingConfig</c>
    /// (<c>thinkingBudget</c> or <c>thinkingLevel</c>); OpenAI <c>reasoning_effort</c>.
    /// </summary>
    [RConfigProperty("settings_thinking")]
    public string? Thinking { get; set; }

    /// <summary>Provider-specific value this model uses for the common word <c>minimal</c> — the floor
    /// (least thinking the model offers), e.g. OpenAI <c>minimal</c>, a small Gemini budget, or (since
    /// Claude has no sub-<c>low</c> tier) Claude <c>low</c>. Null = pass <c>minimal</c> through unchanged
    /// (only valid where the provider accepts it, e.g. OpenAI) — set this for Claude/Gemini.</summary>
    [RConfigProperty("settings_thinking-conversion-minimal")]
    public string? ThinkingConversionMinimal { get; set; }

    /// <summary>Provider-specific value this model uses for the common word <c>low</c> (e.g. <c>2048</c>
    /// for a Gemini token budget, or <c>low</c> for a Claude/OpenAI effort). Null = pass <c>low</c> through.</summary>
    [RConfigProperty("settings_thinking-conversion-low")]
    public string? ThinkingConversionLow { get; set; }

    /// <summary>Provider-specific value this model uses for the common word <c>medium</c>. Null = pass through.</summary>
    [RConfigProperty("settings_thinking-conversion-medium")]
    public string? ThinkingConversionMedium { get; set; }

    /// <summary>Provider-specific value this model uses for the common word <c>high</c>.
    /// Null = pass <c>high</c> through.</summary>
    [RConfigProperty("settings_thinking-conversion-high")]
    public string? ThinkingConversionHigh { get; set; }

    /// <summary>Provider-specific value this model uses for the common word <c>max</c> — the ceiling
    /// (most thinking the model offers), e.g. Claude <c>max</c>, the largest Gemini budget, or (since
    /// OpenAI has no <c>max</c> effort) OpenAI <c>high</c>. Null = pass <c>max</c> through unchanged
    /// (only valid where the provider accepts it, e.g. Claude) — set this for OpenAI/Gemini.</summary>
    [RConfigProperty("settings_thinking-conversion-max")]
    public string? ThinkingConversionMax { get; set; }

    /// <summary>
    /// Translates a thinking amount to this model's provider-specific value. Recognizes the common words
    /// <c>low</c>/<c>medium</c>/<c>high</c> (mapped via the <c>thinking-conversion-*</c> table when set,
    /// else passed through so providers that accept those words directly still work), treats
    /// <c>off</c>/<c>none</c>/<c>disabled</c>/<c>0</c>/<c>false</c> and empty as "no thinking" (returns
    /// null), and passes any other raw value (e.g. a numeric budget or a provider-specific effort) through
    /// unchanged.
    /// </summary>
    /// <param name="amount">The requested amount (common word or raw provider value).</param>
    /// <returns>The provider-specific value to send, or null to disable thinking.</returns>
    public string? ResolveThinking(string? amount)
    {
        if (string.IsNullOrWhiteSpace(amount))
            return null;

        string trimmed = amount.Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "off":
            case "none":
            case "disabled":
            case "0":
            case "false":
                return null;
            case "minimal":
                return string.IsNullOrWhiteSpace(ThinkingConversionMinimal) ? "minimal" : ThinkingConversionMinimal.Trim();
            case "low":
                return string.IsNullOrWhiteSpace(ThinkingConversionLow) ? "low" : ThinkingConversionLow.Trim();
            case "medium":
                return string.IsNullOrWhiteSpace(ThinkingConversionMedium) ? "medium" : ThinkingConversionMedium.Trim();
            case "high":
                return string.IsNullOrWhiteSpace(ThinkingConversionHigh) ? "high" : ThinkingConversionHigh.Trim();
            case "max":
                return string.IsNullOrWhiteSpace(ThinkingConversionMax) ? "max" : ThinkingConversionMax.Trim();
            default:
                return trimmed; // raw value (numeric budget or provider-specific effort)
        }
    }

    // Setting Overrides
    [RConfigProperty("override-settings_filter")]
    public string? Filter { get; set; }
    
    [RConfigProperty("override-settings_chain-of-thought")]
    public string? ChainOfThought { get; set; }
    
    [RConfigProperty("override-settings_request-json")]
    public string? RequestJson { get; set; }
    
    [RConfigProperty("override-settings_guidance-schema-type")]
    public GuidanceSchemaType? GuidanceSchemaType { get; set; }
    
    [RConfigProperty("override-settings_require-valid-output")]
    public bool? RequireValidOutput { get; set; }
    
    [RConfigProperty("override-settings_retry-attempts")]
    public int? RetryAttempts { get; set; }
    
    [RConfigProperty("override-settings_retry-prompt")]
    public string? RetryPrompt { get; set; }
    
    [RConfigProperty("override-settings_few-shot-examples")]
    public int? FewShotExamples { get; set; }
    
    [RConfigProperty("override-settings_best-of")]
    public string? BestOf { get; set; }
    
    [RConfigProperty("override-settings_max-tokens")]
    public string? MaxTokens { get; set; }
    
    [RConfigProperty("override-settings_timeout")]
    public string? Timeout { get; set; }
    
    [RConfigProperty("override-settings_preferred-models")]
    public List<string>? PreferredModels { get; set; }
    
    [RConfigProperty("override-settings_blocked-models")]
    public List<string>? BlockedModels { get; set; }
    
    // Model-level override for Gemini Search Grounding: "true" / "false" / "disabled"
    [RConfigProperty("override-settings_use-search-grounding")]
    public string? UseSearchGrounding { get; set; }
    
    [RConfigProperty("override-settings_min-tier")]
    public ModelTier? MinTier { get; set; }
    
    [RConfigProperty("override-settings_completion-type")]
    public CompletionType? CompletionType { get; set; }
    
    
    // Tuning Overrides
    [RConfigProperty("override-tuning_temperature")]
    public string? Temperature { get; set; }
    
    [RConfigProperty("override-tuning_top-k")]
    public string? TopK { get; set; }
    
    [RConfigProperty("override-tuning_top-p")]
    public string? TopP { get; set; }
    
    [RConfigProperty("override-tuning_min-p")]
    public string? MinP { get; set; }
    
    [RConfigProperty("override-tuning_presence-penalty")]
    public string? PresencePenalty { get; set; }
    
    [RConfigProperty("override-tuning_frequency-penalty")]
    public string? FrequencyPenalty { get; set; }
    
    [RConfigProperty("override-tuning_repetition-penalty")]
    public string? RepetitionPenalty { get; set; }
    
    
    // Input Options
    [RConfigProperty("input_default-system-input-type")]
    public InputType DefaultSystemInputType { get; set; }
    
    // Defaults to Listed so instruction inputs are appended by default (matches the documented default).
    [RConfigProperty("input_default-instruction-input-type")]
    public InputType DefaultInstructionInputType { get; set; } = InputType.Listed;
    
    [RConfigProperty("input_single-item")]
    public string? InputItem { get; set; } 
    
    [RConfigProperty("input_multi-item")]
    public string? InputItemMulti { get; set; } 
    
    
    // Chat Options
    [RConfigProperty("chat-completion_system-message")]
    public bool SystemMessage { get; set; } = true;
    
    [RConfigProperty("chat-completion_prompt-in-system")]
    public bool PromptInSystem { get; set; } = false;
    
    [RConfigProperty("chat-completion_system-in-user")]
    public bool SystemInUser { get; set; } = true;
    
    [RConfigProperty("chat-completion_prompt-in-user")]
    public bool PromptInUser { get; set; } = true;
    
    
    // Completion Template & Options
    [RConfigProperty("prompt-completion_structure")]
    public string? Structure { get; set; }
    
    [RConfigProperty("prompt-completion_system-section")]
    public string? SystemSection { get; set; } 
    
    [RConfigProperty("prompt-completion_instruction-section")]
    public string? InstructionSection { get; set; } 
    
    [RConfigProperty("prompt-completion_input-section")]
    public string? InputSection { get; set; } 
    
    [RConfigProperty("prompt-completion_example-section")]
    public string? ExampleSection { get; set; } 
    
    [RConfigProperty("prompt-completion_example-structure")]
    public string? ExampleStructure { get; set; } 
    
    [RConfigProperty("prompt-completion_example-sub-system")]
    public string? ExampleSubSystem { get; set; } 
    
    [RConfigProperty("prompt-completion_example-sub-instruction")]
    public string? ExampleSubInstruction { get; set; } 
    
    [RConfigProperty("prompt-completion_example-sub-input")]
    public string? ExampleSubInput { get; set; } 
    
    [RConfigProperty("prompt-completion_example-sub-output")]
    public string? ExampleSubOutput { get; set; } 
    
    [RConfigProperty("prompt-completion_output-section")]
    public string? OutputSection { get; set; } 
    
    
    // ==============
    //  Constructors
    // ==============
    
    /// <summary>
    /// Validates required fields after deserialization.
    /// Called automatically by <see cref="RConfigParser"/> via reflection.
    /// Provider resolution is deferred to <see cref="ResolveProvider"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when <see cref="ProviderName"/> is empty or null.</exception>
    public void Init()
    {
        if (string.IsNullOrEmpty(ProviderName))
        {
            Enabled = false;
            throw new ArgumentNullException(nameof(ProviderName), "ProviderName is empty or null!");
        }
    }

    /// <summary>
    /// Resolves the provider reference using the DI provider registry.
    /// Called by <see cref="ModelManagerService"/> after deserialization.
    /// </summary>
    /// <param name="providers">The provider registry to look up the provider from.</param>
    public void ResolveProvider(IProviderManager providers)
    {
        ProviderProfile? foundProvider = providers.Get(ProviderName);
        if (foundProvider is null)
        {
            Enabled = false;
            Util.Log($"Provider '{ProviderName}' could not be found");
            return;
        }
        if (foundProvider.Enabled is false)
        {
            Enabled = false;
            Util.Log($"Provider '{ProviderName}' is not enabled");
            return;
        }
        Provider = foundProvider;
    }
}
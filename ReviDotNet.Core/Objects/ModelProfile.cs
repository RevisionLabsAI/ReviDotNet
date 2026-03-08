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
    /// Indicates whether this model supports legacy prompt completion (non-chat) endpoints.
    /// When set, this value overrides any provider-level defaults for prompt completion support.
    /// </summary>
    [RConfigProperty("settings_supports-prompt-completion")]
    public bool? SupportsPromptCompletion { get; set; }

    /// <summary>
    /// Indicates whether this model supports the newer Responses API completion endpoint.
    /// When set, this value overrides any provider-level defaults for responses completion support.
    /// </summary>
    [RConfigProperty("settings_supports-response-completion")]
    public bool? SupportsResponseCompletion { get; set; }
    
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
    
    [RConfigProperty("input_default-instruction-input-type")]
    public InputType DefaultInstructionInputType { get; set; }
    
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
    
    // Called by "CallInitIfExists" in the "Read()" function of the RConfigParser class
    public void Init()
    {
        if (string.IsNullOrEmpty(ProviderName))
        {
            Enabled = false;
            throw new ArgumentNullException(ProviderName, "ProviderName is empty or null!");
        }

        var foundProvider = ProviderManager.Get(ProviderName);
        if (foundProvider is null)
        {
            Enabled = false;
            Util.Log($"Provider '{ProviderName}' could not be found");
            //throw new ValidationException($"Provider '{ProviderName}' could not be found");
        }
        if (foundProvider.Enabled is false)
        {
            Enabled = false;
            Util.Log($"Provider '{ProviderName}' is not enabled");
            //throw new ValidationException($"Provider '{ProviderName}' is not enabled");
        }

        Provider = foundProvider;
    }
}
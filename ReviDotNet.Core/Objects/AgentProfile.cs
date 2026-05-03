// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Revi;

/// <summary>
/// Represents a fully parsed .agent configuration file.
/// Uses a two-phase custom ToObject() because the state sections are dynamically named
/// (unlike the fixed sections in .pmt files).
///
/// RConfig key format produced by RConfigParser for a .agent file:
///   information_name          → Name
///   information_version       → Version
///   information_description   → Description
///   _system                   → SystemPrompt (raw section)
///   loop_entry                → EntryState
///   state.search_description  → state "search", property description
///   state.search_tools        → state "search", property tools
///   state.search.guardrails_cycle-limit → guardrail property
///   _state.search.instruction → state "search" instruction (raw section)
///   _loop                     → loop DSL text (raw section)
/// </summary>
public class AgentProfile
{
    // ==========================
    //  Properties
    // ==========================

    [RConfigProperty("information_name")]
    public string? Name { get; set; }

    [RConfigProperty("information_version")]
    public int? Version { get; set; }

    [RConfigProperty("information_description")]
    public string? Description { get; set; }

    [RConfigProperty("_system")]
    public string? SystemPrompt { get; set; }

    [RConfigProperty("loop_entry")]
    public string? EntryState { get; set; }

    /// <summary>
    /// Optional run-wide USD cost budget. When set, the runner tracks cumulative cost
    /// across every state activation in the run and refuses an LLM call that would
    /// project to exceed the budget. State-level cost-budget guardrails apply
    /// independently — both must be satisfied for a call to proceed.
    /// </summary>
    [RConfigProperty("settings_cost-budget")]
    public decimal? RunCostBudget { get; set; }

    /// <summary>All declared states, populated by ToObject().</summary>
    public List<AgentState> States { get; set; } = new();

    /// <summary>Parsed loop graph, populated by Init() after ToObject().</summary>
    public List<LoopNode> LoopGraph { get; set; } = new();

    /// <summary>
    /// Cached set of valid signal tokens for each state (uppercase, no nulls).
    /// Populated by Init() after the loop graph is parsed. Used by AgentRunner to
    /// nudge the LLM when it emits a signal not declared from the current state.
    /// </summary>
    public Dictionary<string, IReadOnlySet<string>> ValidSignalsByState { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Stored during ToObject(), consumed by Init()
    private string? _rawLoopDsl;


    // ==========================
    //  Init (called by RConfigParser.ToObject<T> post-deserialization)
    // ==========================

    public void Init()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Agent name must not be null or empty.");

        if (string.IsNullOrWhiteSpace(EntryState))
            throw new ArgumentException($"Agent '{Name}' must declare a loop entry state.");

        if (States.Count == 0)
            throw new ArgumentException($"Agent '{Name}' has no states defined.");

        if (!States.Any(s => string.Equals(s.Name, EntryState, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Agent '{Name}' entry state '{EntryState}' is not defined in [[state.*]] sections.");

        // Parse the loop DSL
        if (!string.IsNullOrWhiteSpace(_rawLoopDsl))
            LoopGraph = LoopDslParser.Parse(_rawLoopDsl);
        else
            Util.Log($"Agent '{Name}': No [[_loop]] section found. Agent will have no state transitions.");

        // Pre-compute valid-signal sets per state for graceful signal validation in AgentRunner.
        ValidSignalsByState = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in LoopGraph)
        {
            var signals = node.Transitions
                .Where(t => !string.IsNullOrEmpty(t.Signal))
                .Select(t => t.Signal!.ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ValidSignalsByState[node.StateName] = signals;
        }
    }


    // ==========================
    //  Custom Deserialization
    // ==========================

    /// <summary>
    /// Two-phase deserialization from the flat RConfigParser dictionary.
    ///
    /// Phase 1: Apply [RConfigProperty] attributes for fixed keys (information, loop, _system).
    /// Phase 2: Regex-scan for state.* keys to build List&lt;AgentState&gt;.
    /// </summary>
    public static AgentProfile ToObject(Dictionary<string, string> data, string? namePrefix = "")
    {
        var profile = new AgentProfile();
        var properties = typeof(AgentProfile).GetProperties();

        // Phase 1: standard attribute-based mapping
        foreach (var property in properties)
        {
            var attr = property.GetCustomAttributes(typeof(RConfigPropertyAttribute), false)
                .FirstOrDefault() as RConfigPropertyAttribute;

            if (attr == null) continue;
            if (!data.TryGetValue(attr.Name, out var value)) continue;

            if (property.Name == "Name" && namePrefix != null)
                value = $"{namePrefix}{value}";

            try
            {
                var converted = RConfigParser.ConvertToType(value, property.PropertyType);
                property.SetValue(profile, converted);
            }
            catch (Exception ex)
            {
                throw new FormatException($"AgentProfile: Failed to convert '{attr.Name}'. Property: {property.Name}", ex);
            }
        }

        // Capture raw loop DSL for Init() to parse
        if (data.TryGetValue("_loop", out var loopDsl))
            profile._rawLoopDsl = loopDsl;

        // Phase 2: discover state names and build AgentState objects
        var stateNameRegex = new Regex(@"^state\.([^_.]+)_", RegexOptions.IgnoreCase);
        var stateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in data.Keys)
        {
            var m = stateNameRegex.Match(key);
            if (m.Success)
                stateNames.Add(m.Groups[1].Value);
        }

        foreach (var stateName in stateNames)
        {
            var state = BuildState(stateName, data);
            profile.States.Add(state);
        }

        // Call Init() to validate and parse the loop graph
        try
        {
            profile.Init();
        }
        catch (Exception e)
        {
            Util.Log($"AgentProfile.Init failed for '{profile.Name}': {e.Message}");
        }

        return profile;
    }


    // ==========================
    //  State Building Helpers
    // ==========================

    private static AgentState BuildState(string stateName, Dictionary<string, string> data)
    {
        var state = new AgentState { Name = stateName };
        string prefix = $"state.{stateName}_";
        string guardrailPrefix = $"state.{stateName}.guardrails_";

        // Build a sub-dictionary for guardrails
        var guardrailData = new Dictionary<string, string>();

        foreach (var (key, value) in data)
        {
            if (key.StartsWith(guardrailPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Strip prefix so RConfigParser.ToObject<AgentGuardrails> can match [RConfigProperty] attributes
                string stripped = key[guardrailPrefix.Length..];
                guardrailData[stripped] = value;
            }
            else if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string field = key[prefix.Length..];
                switch (field.ToLowerInvariant())
                {
                    case "description":
                        state.Description = value;
                        break;
                    case "prompt":
                        state.Prompt = value;
                        break;
                    case "model":
                        state.Model = value;
                        break;
                    case "tools":
                        state.Tools = Util.SplitByCommaOrSpace(value);
                        break;
                }
            }
        }

        // Deserialize guardrails (RConfigParser.ToObject uses [RConfigProperty] attrs)
        if (guardrailData.Count > 0)
            state.Guardrails = RConfigParser.ToObject<AgentGuardrails>(guardrailData) ?? new AgentGuardrails();

        // Instruction is a raw section: key is "_state.<name>.instruction"
        string instructionKey = $"_state.{stateName}.instruction";
        if (data.TryGetValue(instructionKey, out var instruction))
            state.Instruction = instruction;

        // Settings is a raw section: key is "_state.<name>.settings"
        string settingsKey = $"_state.{stateName}.settings";
        if (data.TryGetValue(settingsKey, out var settingsText))
            state.InlineSettings = ParseInlineSettings(settingsText);

        return state;
    }

    // Parses a [[_state.X.settings]] raw section (key = value lines) into a Prompt
    // that holds only the settings/tuning fields. Init() is intentionally skipped.
    private static Prompt ParseInlineSettings(string settingsText)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in settingsText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            if (!string.IsNullOrEmpty(key))
                dict[$"settings_{key}"] = val;
        }

        var settings = new Prompt();
        foreach (var prop in typeof(Prompt).GetProperties())
        {
            var attr = prop.GetCustomAttributes(typeof(RConfigPropertyAttribute), false)
                .FirstOrDefault() as RConfigPropertyAttribute;
            if (attr == null || !dict.TryGetValue(attr.Name, out var val)) continue;
            try { prop.SetValue(settings, RConfigParser.ConvertToType(val, prop.PropertyType)); }
            catch { /* skip unrecognized or malformed settings */ }
        }
        return settings;
    }
}

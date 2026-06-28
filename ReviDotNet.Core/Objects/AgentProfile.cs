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

    /// <summary>
    /// How the agent may be driven from the workshop: <c>fixed</c> (autonomous run), <c>chat</c>
    /// (interactive), or <c>both</c>. Null when unspecified — see <see cref="EffectiveInteractionMode"/>,
    /// which defaults to <see cref="Revi.InteractionMode.Fixed"/> for backward compatibility.
    /// </summary>
    [RConfigProperty("settings_interaction-mode")]
    public InteractionMode? InteractionMode { get; set; }

    /// <summary>The resolved interaction mode — <see cref="Revi.InteractionMode.Fixed"/> when unset.</summary>
    public InteractionMode EffectiveInteractionMode => InteractionMode ?? Revi.InteractionMode.Fixed;

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

    /// <summary>
    /// Absolute path of the <c>.agent</c> file this profile was loaded from on disk, or null when it
    /// came from an embedded resource or an in-memory edit. Runtime metadata (not a config field): the
    /// loader stamps it so tools can read back / write the original source even when the file lives
    /// outside the app's own RConfigs (e.g. an additional RConfig folder).
    /// </summary>
    public string? SourcePath { get; set; }

    // Stored during ToObject(), consumed by Init()
    private string? _rawLoopDsl;

    /// <summary>
    /// Canonical state-name grammar: a letter followed by letters, digits, or hyphens. Underscores are
    /// forbidden because state discovery splits keys on '_', so an underscore'd state name is undiscoverable.
    /// Shared by <see cref="ValidateGraph"/> (and mirrored by the REVI011 analyzer).
    /// </summary>
    internal static readonly Regex ValidStateName = new(@"^[A-Za-z][A-Za-z0-9-]*$", RegexOptions.Compiled);


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

        // Surface graph mistakes at load time (warn-only — never blocks loading).
        foreach (var warning in ValidateGraph())
            Util.Log($"Warning: {warning}");
    }

    /// <summary>
    /// Validates the parsed loop graph against the discovered states and returns human-readable warnings
    /// (never throws). Catches: loop nodes / transition targets referencing an undefined state, state names
    /// with an illegal grammar (e.g. underscores), dead edges after an unconditional fallback, and duplicate
    /// signal tokens within a state. Returns an empty list for a clean graph.
    /// </summary>
    public List<string> ValidateGraph()
    {
        var warnings = new List<string>();
        var discovered = new HashSet<string>(States.Select(s => s.Name ?? ""), StringComparer.OrdinalIgnoreCase);

        bool IsSpecial(string target) =>
            target.Equals("[end]", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("self", StringComparison.OrdinalIgnoreCase);

        foreach (var node in LoopGraph)
        {
            string state = node.StateName;

            if (!ValidStateName.IsMatch(state))
                warnings.Add($"Agent '{Name}': loop state name '{state}' is invalid — state names must be letters, digits, and hyphens (no underscores), or it cannot be discovered from its [[state.*]] section.");
            else if (!discovered.Contains(state))
                warnings.Add($"Agent '{Name}': loop declares state '{state}' which has no matching [[state.{state}]] definition.");

            bool sawUnconditional = false;
            var seenSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in node.Transitions)
            {
                string target = t.TargetState ?? "";

                if (sawUnconditional)
                    warnings.Add($"Agent '{Name}': in state '{state}', the transition to '{target}' is unreachable — it comes after an unconditional (no [when:]) fallback.");

                if (!IsSpecial(target))
                {
                    if (!ValidStateName.IsMatch(target))
                        warnings.Add($"Agent '{Name}': in state '{state}', transition target '{target}' is not a valid state name (letters, digits, hyphens; no underscores).");
                    else if (!discovered.Contains(target))
                        warnings.Add($"Agent '{Name}': in state '{state}', transition target '{target}' is not a defined state.");
                }

                if (string.IsNullOrEmpty(t.Signal))
                    sawUnconditional = true;
                else if (!seenSignals.Add(t.Signal))
                    warnings.Add($"Agent '{Name}': in state '{state}', signal '{t.Signal}' is declared more than once — only the first transition for it is reachable.");
            }
        }

        return warnings;
    }

    /// <summary>
    /// Warns about state sections (<c>[[_state.X.instruction]]</c>, <c>[[_state.X.settings]]</c>,
    /// <c>[[state.X.guardrails]]</c>) whose state name was never discovered as a plain
    /// <c>state.X_&lt;field&gt;</c> key — typically because the name contains an underscore or the state has
    /// no plain field. Such a state is silently ignored. Returns warnings (does not log).
    /// </summary>
    internal static List<string> CollectDiscoveryWarnings(
        Dictionary<string, string> data, ISet<string> discoveredStates, string? agentName)
    {
        var warnings = new List<string>();
        var sectionRefs = new[]
        {
            new Regex(@"^_state\.(?<n>.+?)\.(?:instruction|settings)$", RegexOptions.IgnoreCase),
            new Regex(@"^state\.(?<n>.+?)\.guardrails_", RegexOptions.IgnoreCase),
        };

        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in data.Keys)
        {
            foreach (var rx in sectionRefs)
            {
                var m = rx.Match(key);
                if (!m.Success) continue;
                string name = m.Groups["n"].Value;
                if (!discoveredStates.Contains(name) && flagged.Add(name))
                    warnings.Add($"Agent '{agentName}': a section for state '{name}' exists but no discoverable 'state.{name}_<field>' key was found, so the state is ignored (state names cannot contain underscores).");
            }
        }

        return warnings;
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

        // Warn about state sections whose name was never discovered (underscore'd / fieldless states).
        foreach (var warning in CollectDiscoveryWarnings(data, stateNames, profile.Name))
            Util.Log($"Warning: {warning}");

        // Call Init() to validate and parse the loop graph
        try
        {
            profile.Init();
        }
        catch (Exception e)
        {
            Util.Log($"Warning: AgentProfile.Init failed for '{profile.Name}': {e.Message}");
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
            {
                // A .pmt key maps to EITHER a settings_* or a tuning_* property on Prompt, and the two key
                // namespaces don't overlap. Register both prefixes so tuning keys (temperature, top-p,
                // penalties, …) bind here too — not just the settings_* keys (max-tokens/best-of/use-search-grounding).
                dict[$"settings_{key}"] = val;
                dict[$"tuning_{key}"] = val;
            }
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

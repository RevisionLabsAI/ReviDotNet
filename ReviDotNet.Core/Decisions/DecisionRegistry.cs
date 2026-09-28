using System.Reflection;
using System.Collections.Concurrent;

namespace Revi;

/// <summary>Provider-independent configuration for a decision model.</summary>
public sealed class DecisionModelProfile
{
    /// <summary>Declared profile name.</summary>
    [RConfigProperty("general_name")] public string Name { get; set; } = "";
    /// <summary>Whether this profile may be called.</summary>
    [RConfigProperty("general_enabled")] public bool Enabled { get; set; } = true;
    /// <summary>Provider model identifier, preferably pinned when calibrated.</summary>
    [RConfigProperty("general_model-string")] public string ModelString { get; set; } = "";
    /// <summary>Whether ModelString is an immutable revision, enabling per-run selector caching.</summary>
    [RConfigProperty("general_revision-pinned")] public bool RevisionPinned { get; set; }
    /// <summary>Provider registry reference.</summary>
    [RConfigProperty("general_provider-name")] public string ProviderName { get; set; } = "";
    /// <summary>Maximum choice options supported by this model.</summary>
    [RConfigProperty("limits_maximum-choice-options")] public int MaximumChoiceOptions { get; set; } = 255;
    /// <summary>Maximum score levels supported by this model.</summary>
    [RConfigProperty("limits_maximum-score-levels")] public int MaximumScoreLevels { get; set; } = 10;
    /// <summary>Conservative UTF-8-byte request estimate; provider remains authoritative for tokenization.</summary>
    [RConfigProperty("limits_context-window")] public int ContextWindow { get; set; } = 64000;
    /// <summary>Conservative state plus longest question estimate.</summary>
    [RConfigProperty("limits_state-plus-longest-question")] public int StatePlusLongestQuestion { get; set; } = 32000;
    /// <summary>USD per million reported input tokens.</summary>
    [RConfigProperty("pricing_input-per-million")] public decimal InputPerMillion { get; set; }
    /// <summary>USD per million reported output tokens.</summary>
    [RConfigProperty("pricing_output-per-million")] public decimal OutputPerMillion { get; set; }
}

/// <summary>Registry for decision models and human-authored question packs.</summary>
public interface IDecisionRegistry
{
    /// <summary>Gets a named prompt or throws a configuration error.</summary>
    DecisionPrompt GetPrompt(string name);
    /// <summary>Gets a named model or throws a configuration error.</summary>
    DecisionModelProfile GetModel(string name);
    /// <summary>Lists question packs for editors.</summary>
    IReadOnlyList<DecisionPrompt> GetPrompts();
    /// <summary>Adds a question pack; only a newer version may replace different content.</summary>
    void Add(DecisionPrompt prompt);
    /// <summary>Adds a model profile.</summary>
    void Add(DecisionModelProfile model);
    /// <summary>Clears both registries for an explicit reload.</summary>
    void Reset();
    /// <summary>Additively loads embedded resources.</summary>
    void LoadAssembly(Assembly assembly);
    /// <summary>Additively loads an RConfigs root.</summary>
    void LoadDirectory(string root);
}

/// <summary>Decision registry with strict duplicate/version validation and no implicit folder prefixes.</summary>
public sealed class DecisionRegistry : IDecisionRegistry
{
    private readonly ConcurrentDictionary<string, DecisionPrompt> _prompts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DecisionModelProfile> _models = new(StringComparer.Ordinal);
    /// <inheritdoc/>
    public DecisionPrompt GetPrompt(string name) => _prompts.TryGetValue(ConfigName.Resolve(name), out DecisionPrompt? prompt) ? prompt : throw new KeyNotFoundException($"Decision prompt '{name}' is not registered.");
    /// <inheritdoc/>
    public DecisionModelProfile GetModel(string name) => _models.TryGetValue(ConfigName.Resolve(name), out DecisionModelProfile? model) ? model : throw new KeyNotFoundException($"Decision model '{name}' is not registered.");
    /// <inheritdoc/>
    public IReadOnlyList<DecisionPrompt> GetPrompts() => _prompts.Values.ToArray();
    /// <inheritdoc/>
    public void Add(DecisionPrompt prompt)
    {
        ConfigName.Resolve(prompt.Name);
        _prompts.AddOrUpdate(prompt.Name, prompt, (_, previous) =>
        {
            if (previous.Version > prompt.Version) return previous;
            if (previous.Version == prompt.Version)
            {
                if (previous.Hash != prompt.Hash || previous.Model != prompt.Model) throw new InvalidOperationException($"Conflicting decision prompt '{prompt.Name}' version {prompt.Version}.");
                if (prompt.SourceText is null) return previous;
                // Same pack from a source without a file (e.g. the embedded copy after the output directory's):
                // keep the on-disk entry, so the editor shows that file's text and saves back to its path.
                return prompt.SourcePath is null && previous.SourcePath is not null ? previous : prompt;
            }
            return prompt;
        });
    }
    /// <inheritdoc/>
    public void Add(DecisionModelProfile model)
    {
        ConfigName.Resolve(model.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.ProviderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.ModelString);
        if (model.InputPerMillion < 0 || model.OutputPerMillion < 0 || model.MaximumChoiceOptions < 1 || model.MaximumScoreLevels < 2 || model.ContextWindow < 1 || model.StatePlusLongestQuestion < 1)
            throw new ArgumentException("Invalid decision model limits or pricing.");
        _models.AddOrUpdate(model.Name, model, (_, previous) =>
        {
            if (System.Text.Json.JsonSerializer.Serialize(previous) != System.Text.Json.JsonSerializer.Serialize(model))
                throw new InvalidOperationException($"Conflicting decision model '{model.Name}'.");
            return previous;
        });
    }
    /// <inheritdoc/>
    public void Reset() { _prompts.Clear(); _models.Clear(); }
    /// <inheritdoc/>
    public void LoadAssembly(Assembly assembly)
    {
        foreach (string name in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            bool prompt = name.Contains(".Decisions.") && name.EndsWith(".decision", StringComparison.OrdinalIgnoreCase);
            bool model = name.Contains(".Models.Decision.") && name.EndsWith(".rcfg", StringComparison.OrdinalIgnoreCase);
            if (!prompt && !model) continue;
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using StreamReader reader = new(stream);
            string text = reader.ReadToEnd();
            if (prompt) Add(DecisionPromptParser.Parse(text));
            else Add(RConfigParser.ToObject<DecisionModelProfile>(RConfigParser.ReadEmbedded(text))!);
        }
    }
    /// <inheritdoc/>
    public void LoadDirectory(string root)
    {
        string prompts = Path.Combine(root, "Decisions");
        string models = Path.Combine(root, "Models", "Decision");
        if (Directory.Exists(models))
            foreach (string path in Directory.EnumerateFiles(models, "*.rcfg", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                Add(RConfigParser.ToObject<DecisionModelProfile>(RConfigParser.Read(path))!);
        if (Directory.Exists(prompts))
            foreach (string path in Directory.EnumerateFiles(prompts, "*.decision", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                Add(DecisionPromptParser.Parse(File.ReadAllText(path)) with { SourcePath = Path.GetFullPath(path) });
    }
}

using System.Text;
using Revi;

namespace ReviDotNet.Forge.Services;

/// <summary>Edits original RConfig decision files, preserving the embedded YAML block and authored comments.</summary>
public sealed class DecisionEditorService(IConfiguration configuration, IDecisionRegistry registry, ArtifactHistoryService? history = null)
{
    private readonly object _saveLock = new();
    /// <summary>Lists loaded packs.</summary>
    public IReadOnlyList<DecisionPrompt> GetAll() => registry.GetPrompts();
    /// <summary>Returns the original authored text, including comments and block formatting.</summary>
    public string Read(string name) => registry.GetPrompt(name).SourceText ?? throw new InvalidOperationException("This programmatic question pack has no authored source.");
    /// <summary>Validates syntax and schema without changing disk or registry state.</summary>
    public DecisionPrompt Validate(string text) => DecisionPromptParser.Parse(text);
    /// <summary>Saves validated, versioned text beneath the configured Decisions root.</summary>
    public void Save(string text)
    {
        lock (_saveLock) SaveCore(text);
    }

    /// <summary>Serializes editor writes so version validation and replacement share a critical section.</summary>
    private void SaveCore(string text)
    {
        DecisionPrompt prompt = Validate(text);
        DecisionPrompt? existing = registry.GetPrompts().FirstOrDefault(p => p.Name == prompt.Name);
        if (existing is not null && (prompt.Version < existing.Version || prompt.Version == existing.Version && (prompt.Hash != existing.Hash || prompt.Model != existing.Model)))
            throw new InvalidOperationException("Increase the prompt version before changing its questions or model.");
        string root = Path.GetFullPath(configuration["Forge:DecisionsSourcePath"] ?? "RConfigs/Decisions");
        string path = Path.GetFullPath(Path.Combine(root, prompt.Name.Replace('/', Path.DirectorySeparatorChar) + ".decision"));
        if (existing?.SourcePath is string source && Path.GetFullPath(source).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            path = Path.GetFullPath(source);
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Decision file must stay inside its configured source directory.");
        for (string? check = path; check is not null; check = Path.GetDirectoryName(check))
            if ((File.Exists(check) || Directory.Exists(check)) && (File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Decision source paths cannot traverse symbolic links.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) history?.Snapshot("decision", prompt.Name, File.ReadAllText(path));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        registry.Add(prompt with { SourcePath = path });
    }
}

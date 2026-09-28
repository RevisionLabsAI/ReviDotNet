namespace Revi;

/// <summary>On-demand discovery within the current state's executable authorized tool catalog.</summary>
public sealed class ToolSearchTool : IBuiltInTool
{
    /// <inheritdoc/>
    public string Name => "tool-search";
    /// <inheritdoc/>
    public string Description => "Finds additional tools authorized in this state. Input: keywords, or empty to list all. This only returns capability descriptions; it never grants access.";
    /// <inheritdoc/>
    public Task<ToolCallResult> ExecuteAsync(string input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IReadOnlyList<ContextCandidate> catalog = AgentRunContext.Current?.AuthorizedContext ?? [];
        string[] terms = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IEnumerable<ContextCandidate> matches = terms.Length == 0 ? catalog : catalog.Where(c => terms.Any(t => (c.Id + " " + c.Description).Contains(t, StringComparison.OrdinalIgnoreCase)));
        return Task.FromResult(new ToolCallResult { ToolName = Name, Output = System.Text.Json.JsonSerializer.Serialize(matches.Select(c => new { name = c.Id, description = c.Description })) });
    }
}

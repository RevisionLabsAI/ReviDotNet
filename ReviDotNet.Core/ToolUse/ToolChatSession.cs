// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;

namespace Revi.ToolUse;

/// <summary>What the gate decided about a tool call before it runs.</summary>
public enum ToolGateDecision
{
    /// <summary>Run it now.</summary>
    Allow,

    /// <summary>Pause the turn and ask the user first.</summary>
    Confirm,

    /// <summary>Refuse it; the model is told why.</summary>
    Deny
}

/// <summary>The gate's verdict on one call.</summary>
/// <param name="Decision">The decision.</param>
/// <param name="Summary">For <see cref="ToolGateDecision.Confirm"/>: what the call would do, in the user's words.</param>
/// <param name="Reason">For <see cref="ToolGateDecision.Deny"/>: why, in words the model can act on.</param>
public sealed record ToolGateResult(ToolGateDecision Decision, string? Summary = null, string? Reason = null)
{
    /// <summary>Run the call.</summary>
    public static ToolGateResult Allow { get; } = new(ToolGateDecision.Allow);

    /// <summary>Ask first.</summary>
    public static ToolGateResult Confirm(string summary) => new(ToolGateDecision.Confirm, summary);

    /// <summary>Refuse.</summary>
    public static ToolGateResult Deny(string reason) => new(ToolGateDecision.Deny, Reason: reason);
}

/// <summary>Runs the tools a session's model asks for, after gating each call.</summary>
public interface IToolExecutor
{
    /// <summary>Decides whether a call may run, must be confirmed, or is refused. Called before every call.</summary>
    ToolGateResult Gate(ToolUseRequest request);

    /// <summary>Runs an allowed or confirmed call. Exceptions become error results for the model.</summary>
    Task<ToolResult> ExecuteAsync(ToolUseRequest request, CancellationToken cancellationToken);
}

/// <summary>The settings a session applies to every request. Mutable so a caller can switch model or effort between turns.</summary>
public sealed class ToolChatSessionOptions
{
    /// <summary>The model id.</summary>
    public required string Model { get; set; }

    /// <summary>The system prompt. Keep it stable; put per-turn facts in a system note instead.</summary>
    public string? System { get; set; }

    /// <summary>The tools, in a stable order.</summary>
    public IReadOnlyList<ToolDefinition> Tools { get; set; } = Array.Empty<ToolDefinition>();

    /// <summary>The thinking mode.</summary>
    public ThinkingMode Thinking { get; set; } = ThinkingMode.Adaptive;

    /// <summary>The effort level, or null for the provider default.</summary>
    public string? Effort { get; set; }

    /// <summary>Whether thinking summaries are streamed.</summary>
    public bool ShowThinking { get; set; }

    /// <summary>The output token ceiling per request.</summary>
    public int MaxTokens { get; set; } = 16_000;

    /// <summary>Beta flags.</summary>
    public IReadOnlyList<string> Betas { get; set; } = Array.Empty<string>();

    /// <summary>Whether to route around safety refusals server-side.</summary>
    public bool UseRefusalFallbacks { get; set; }

    /// <summary>Opaque per-user id for the provider's metadata.</summary>
    public string? UserId { get; set; }

    /// <summary>How many model requests one turn may make before the session stops it. Default 25.</summary>
    public int MaxRoundsPerTurn { get; set; } = 25;

    /// <summary>Whether tool calls of one message run concurrently. Default true.</summary>
    public bool ParallelTools { get; set; } = true;

    /// <summary>Whether the latest user message carries a cache breakpoint, so the next turn reuses the whole history. Default true.</summary>
    public bool CacheConversation { get; set; } = true;

    /// <summary>When true the model may call at most one tool per message.</summary>
    public bool DisableParallelToolUse { get; set; }
}

/// <summary>
/// A tool-using conversation: it keeps the history, sends each turn, runs the tools the model asks for
/// through an <see cref="IToolExecutor"/>, and pauses when a call needs the user's confirmation. The
/// history is append-only, which the provider's thinking blocks require.
/// </summary>
public sealed class ToolChatSession
{
    private readonly ToolChatClient _client;
    private readonly IToolExecutor _executor;
    private readonly List<ChatMessage> _messages = new();
    private readonly object _gate = new();
    private bool _busy;

    // Set while the turn waits for the user: the calls needing a decision, and the results already
    // computed for the other calls of the same assistant message, keyed by call id.
    private List<PendingToolCall>? _pending;
    private Dictionary<string, ToolResult>? _pendingSettled;
    private IReadOnlyList<ToolUseRequest>? _pendingOrder;

    /// <summary>Creates a session.</summary>
    public ToolChatSession(ToolChatClient client, ToolChatSessionOptions options, IToolExecutor executor)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    /// <summary>The settings applied to the next request.</summary>
    public ToolChatSessionOptions Options { get; }

    /// <summary>The history, oldest first. Persist it with <see cref="ChatMessage.ToJson"/>.</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>Usage summed over every request of the session.</summary>
    public ChatUsage TotalUsage { get; private set; } = ChatUsage.Zero;

    /// <summary>Usage of the most recent turn.</summary>
    public ChatUsage LastTurnUsage { get; private set; } = ChatUsage.Zero;

    /// <summary>The calls waiting for the user, or empty.</summary>
    public IReadOnlyList<PendingToolCall> Pending => _pending ?? (IReadOnlyList<PendingToolCall>)Array.Empty<PendingToolCall>();

    /// <summary>Whether a turn is waiting for <see cref="ResolvePendingAsync"/>.</summary>
    public bool IsAwaitingConfirmation => _pending is { Count: > 0 };

    /// <summary>Whether a turn is in progress.</summary>
    public bool IsBusy => _busy;

    /// <summary>Restores a history (from <see cref="ChatMessage.ToJson"/>) into an empty session.</summary>
    public void Restore(IEnumerable<ChatMessage> messages)
    {
        if (_messages.Count > 0) throw new InvalidOperationException("The session already has messages.");
        _messages.AddRange(messages);
    }

    /// <summary>Sends a user message and runs the turn to completion or to a confirmation pause.</summary>
    /// <param name="userText">What the user said.</param>
    /// <param name="systemNote">An optional operator note for this turn (facts of the moment), placed after the user message.</param>
    /// <param name="cancellationToken">Cancels the turn; the history keeps whatever completed.</param>
    public IAsyncEnumerable<ToolChatEvent> SendAsync(string userText, string? systemNote = null, CancellationToken cancellationToken = default)
        => SendAsync(ChatMessage.User(userText), systemNote, cancellationToken);

    /// <summary>Sends a user message (text, images) and runs the turn.</summary>
    public async IAsyncEnumerable<ToolChatEvent> SendAsync(ChatMessage userMessage, string? systemNote = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (userMessage.Role != "user") throw new ArgumentException("Only a user message starts a turn.", nameof(userMessage));
        Begin();
        try
        {
            if (IsAwaitingConfirmation)
            {
                // The user moved on without answering: every pending call is declined, and the new
                // message rides in the same user message after the tool results, which keeps the
                // history valid (a tool call is always answered) and tells the model what happened.
                var declined = SettlePending(_ => (false, "The user did not confirm this and said something else instead."));
                var combined = new List<JObject>(declined.Blocks);
                combined.AddRange(userMessage.Blocks);
                userMessage = new ChatMessage("user", combined);
                ClearPending();
            }
            PlaceCacheBreakpoint(userMessage);
            _messages.Add(userMessage);
            if (!string.IsNullOrWhiteSpace(systemNote)) _messages.Add(ChatMessage.SystemNote(systemNote!));
            LastTurnUsage = ChatUsage.Zero;
            await foreach (ToolChatEvent e in RunLoopAsync(cancellationToken)) yield return e;
        }
        finally { End(); }
    }

    /// <summary>
    /// Answers the pending confirmations and continues the paused turn. Calls not named are declined.
    /// </summary>
    /// <param name="decisions">Call id → approved. A declined call's result says so.</param>
    /// <param name="declineNote">What the model is told about declined calls.</param>
    /// <param name="cancellationToken">Cancels the turn; the history keeps whatever completed.</param>
    public async IAsyncEnumerable<ToolChatEvent> ResolvePendingAsync(IReadOnlyDictionary<string, bool> decisions, string? declineNote = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsAwaitingConfirmation) throw new InvalidOperationException("Nothing is awaiting confirmation.");
        Begin();
        try
        {
            List<PendingToolCall> pending = _pending!;
            var approved = pending.Where(p => decisions.TryGetValue(p.Request.Id, out bool ok) && ok).ToList();
            var results = new Dictionary<string, ToolResult>(_pendingSettled!);
            foreach (PendingToolCall p in pending.Except(approved))
                results[p.Request.Id] = new ToolResult(p.Request.Id, declineNote ?? "The user declined this action.", IsError: true);

            foreach (ToolUseRequest request in approved.Select(p => p.Request))
                yield return new ToolCallStartedEvent(request);
            var executed = await ExecuteAsync(approved.Select(p => p.Request).ToList(), cancellationToken);
            foreach (var (request, result, elapsed) in executed)
            {
                results[request.Id] = result;
                yield return new ToolCallFinishedEvent(request, result, elapsed);
            }

            var ordered = _pendingOrder!.Select(r => results[r.Id]).ToList();
            ClearPending();
            var message = ChatMessage.ToolResults(ordered);
            PlaceCacheBreakpoint(message);
            _messages.Add(message);
            await foreach (ToolChatEvent e in RunLoopAsync(cancellationToken)) yield return e;
        }
        finally { End(); }
    }

    private async IAsyncEnumerable<ToolChatEvent> RunLoopAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int rounds = 0;
        while (true)
        {
            if (++rounds > Options.MaxRoundsPerTurn)
            {
                yield return new TurnCompletedEvent("round_limit", LastTurnUsage, LastAssistantText());
                yield break;
            }

            ToolChatResponse? response = null;
            await foreach (ToolChatEvent e in _client.StreamAsync(BuildRequest(), cancellationToken))
            {
                if (e is MessageCompletedEvent done) response = done.Response;
                yield return e;
            }
            if (response is null) throw new InvalidOperationException("The stream ended without a completed message.");

            _messages.Add(response.Message);
            LastTurnUsage += response.Usage;
            TotalUsage += response.Usage;

            if (response.StopReason == "pause_turn")
                continue; // A server tool is mid-work; the next request resumes it.

            if (!response.NeedsToolResults)
            {
                yield return new TurnCompletedEvent(response.StopReason, LastTurnUsage, response.Text);
                yield break;
            }

            // Gate every call first, so the user sees all confirmations of one message together.
            var settled = new Dictionary<string, ToolResult>();
            var toRun = new List<ToolUseRequest>();
            var toConfirm = new List<PendingToolCall>();
            foreach (ToolUseRequest request in response.ToolUses)
            {
                if (request.Input["__invalid_json"] is not null)
                {
                    settled[request.Id] = new ToolResult(request.Id, "The tool input was not valid JSON. Call the tool again with a complete input.", IsError: true);
                    continue;
                }
                ToolGateResult gate;
                try { gate = _executor.Gate(request); }
                catch (Exception e) { gate = ToolGateResult.Deny("The gate failed: " + e.Message); }
                switch (gate.Decision)
                {
                    case ToolGateDecision.Allow: toRun.Add(request); break;
                    case ToolGateDecision.Confirm: toConfirm.Add(new PendingToolCall(request, gate.Summary ?? request.Name)); break;
                    default: settled[request.Id] = new ToolResult(request.Id, "Not allowed: " + (gate.Reason ?? "this action is not permitted."), IsError: true); break;
                }
            }

            foreach (ToolUseRequest request in toRun) yield return new ToolCallStartedEvent(request);
            foreach (var (request, result, elapsed) in await ExecuteAsync(toRun, cancellationToken))
            {
                settled[request.Id] = result;
                yield return new ToolCallFinishedEvent(request, result, elapsed);
            }

            if (toConfirm.Count > 0)
            {
                _pending = toConfirm;
                _pendingSettled = settled;
                _pendingOrder = response.ToolUses;
                yield return new ConfirmationRequiredEvent(toConfirm);
                yield break;
            }

            var message = ChatMessage.ToolResults(response.ToolUses.Select(r => settled[r.Id]));
            PlaceCacheBreakpoint(message);
            _messages.Add(message);
        }
    }

    private async Task<List<(ToolUseRequest Request, ToolResult Result, TimeSpan Elapsed)>> ExecuteAsync(IReadOnlyList<ToolUseRequest> requests, CancellationToken cancellationToken)
    {
        var results = new List<(ToolUseRequest, ToolResult, TimeSpan)>();
        if (requests.Count == 0) return results;
        if (Options.ParallelTools && requests.Count > 1)
        {
            var tasks = requests.Select(r => RunOneAsync(r, cancellationToken)).ToList();
            results.AddRange(await Task.WhenAll(tasks));
        }
        else
        {
            foreach (ToolUseRequest request in requests) results.Add(await RunOneAsync(request, cancellationToken));
        }
        return results;
    }

    private async Task<(ToolUseRequest, ToolResult, TimeSpan)> RunOneAsync(ToolUseRequest request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await _executor.ExecuteAsync(request, cancellationToken);
            if (result.ToolUseId != request.Id) result = result with { ToolUseId = request.Id };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Util.Log($"ToolChatSession: tool '{request.Name}' threw: {e.Message}");
            result = new ToolResult(request.Id, "The tool failed: " + e.Message, IsError: true);
        }
        return (request, result, watch.Elapsed);
    }

    private ChatMessage SettlePending(Func<PendingToolCall, (bool Approved, string Note)> decide)
    {
        var results = new Dictionary<string, ToolResult>(_pendingSettled!);
        foreach (PendingToolCall p in _pending!)
        {
            var (_, note) = decide(p);
            results[p.Request.Id] = new ToolResult(p.Request.Id, note, IsError: true);
        }
        return ChatMessage.ToolResults(_pendingOrder!.Select(r => results[r.Id]));
    }

    private void ClearPending()
    {
        _pending = null;
        _pendingSettled = null;
        _pendingOrder = null;
    }

    private ToolChatRequest BuildRequest() => new()
    {
        Model = Options.Model,
        System = Options.System,
        Messages = _messages.ToList(),
        Tools = Options.Tools,
        Thinking = Options.Thinking,
        Effort = Options.Effort,
        ShowThinking = Options.ShowThinking,
        MaxTokens = Options.MaxTokens,
        Betas = Options.Betas,
        UseRefusalFallbacks = Options.UseRefusalFallbacks,
        UserId = Options.UserId,
        DisableParallelToolUse = Options.DisableParallelToolUse
    };

    private void PlaceCacheBreakpoint(ChatMessage newest)
    {
        if (!Options.CacheConversation) return;
        // One moving breakpoint on the newest user message: earlier breakpoints are dropped so the
        // request never exceeds the provider's limit of four, and the prefix up to the previous
        // breakpoint still matches the cache.
        foreach (ChatMessage m in _messages) if (m.Role == "user") m.ClearCacheBreakpoint();
        newest.MarkCacheBreakpoint();
    }

    private string LastAssistantText()
        => _messages.LastOrDefault(m => m.Role == "assistant")?.Text ?? string.Empty;

    private void Begin()
    {
        lock (_gate)
        {
            if (_busy) throw new InvalidOperationException("A turn is already in progress.");
            _busy = true;
        }
    }

    private void End()
    {
        lock (_gate) _busy = false;
    }
}

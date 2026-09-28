using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Revi;

/// <summary>A provider-neutral question over the shared state of a decision request.</summary>
public abstract record DecisionQuestion(object Instructions)
{
    /// <summary>The SystemOne primitive name.</summary>
    public abstract string Kind { get; }
    /// <summary>The rubric sent to the transport.</summary>
    public abstract object? Criteria { get; }
}

/// <summary>Selects one member of a closed set. Include an explicit unknown option for open-world tasks.</summary>
public sealed record ChoiceQuestion(object Instructions, IReadOnlyDictionary<string, object?> Options)
    : DecisionQuestion(Instructions)
{
    /// <inheritdoc/>
    public override string Kind => "choice";
    /// <inheritdoc/>
    public override object Criteria => Options;
}

/// <summary>Estimates the probability that a proposition is true.</summary>
public record BooleanQuestion(object Instructions, IReadOnlyDictionary<string, object?>? Meanings = null)
    : DecisionQuestion(Instructions)
{
    /// <inheritdoc/>
    public override string Kind => "noul";
    /// <inheritdoc/>
    public override object? Criteria => Meanings;
}

/// <summary>SystemOne terminology alias for <see cref="BooleanQuestion"/>.</summary>
public sealed record NoulQuestion(object Instructions, IReadOnlyDictionary<string, object?>? Meanings = null)
    : BooleanQuestion(Instructions, Meanings);

/// <summary>Rates state against ordered levels, indexed from zero.</summary>
public sealed record ScoreQuestion(object Instructions, IReadOnlyList<object> Levels) : DecisionQuestion(Instructions)
{
    /// <inheritdoc/>
    public override string Kind => "score";
    /// <inheritdoc/>
    public override object Criteria => Levels;
}

/// <summary>A typed answer; probability is evidence for a policy, never authorization.</summary>
public abstract record DecisionAnswer;

/// <summary>A closed-set choice with the complete distribution.</summary>
public sealed record ChoiceAnswer(string Value, double Confidence, IReadOnlyDictionary<string, double> Probabilities)
    : DecisionAnswer;

/// <summary>A choice mapped to named enum constants, retaining probabilities.</summary>
public sealed record ChoiceAnswer<T>(T Value, double Confidence, IReadOnlyDictionary<T, double> Probabilities)
    where T : struct, Enum;

/// <summary>A probability of yes; no implicit threshold or conversion to bool.</summary>
public record BooleanAnswer(double Probability) : DecisionAnswer;

/// <summary>SystemOne name for a probability of yes.</summary>
public sealed record NoulAnswer(double Probability) : BooleanAnswer(Probability);

/// <summary>A probability-weighted score and the full ordered rubric distribution.</summary>
public sealed record ScoreAnswer(double Value, double Confidence, IReadOnlyDictionary<string, double> Probabilities,
    IReadOnlyDictionary<string, string> Legend) : DecisionAnswer;

/// <summary>Provider-reported usage; tokens may be reported even when priced at zero.</summary>
public sealed record DecisionUsage(long InputTokens, long OutputTokens);

/// <summary>A complete decision response, with identities suitable for calibration and audit.</summary>
public sealed record DecisionRun(
    string Model, IReadOnlyDictionary<string, DecisionAnswer> Answers, DecisionUsage Usage)
{
    /// <summary>Unique invocation identity for idempotent usage recording.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Declared prompt identifier.</summary>
    public string PromptName { get; init; } = "";
    /// <summary>Declared prompt version.</summary>
    public int PromptVersion { get; init; }
    /// <summary>Hash of the effective question pack, including runtime candidate criteria.</summary>
    public string PromptHash { get; init; } = "";
    /// <summary>Configured provider identifier.</summary>
    public string ProviderName { get; init; } = "";
    /// <summary>Configured model profile identifier.</summary>
    public string ModelProfile { get; init; } = "";
    /// <summary>Elapsed time including retries.</summary>
    public TimeSpan Elapsed { get; init; }
    /// <summary>Cost calculated from configured prices and reported usage, in USD.</summary>
    public decimal Cost { get; init; }
    /// <summary>Gets a named raw choice.</summary>
    public ChoiceAnswer Choice(string name) => Get<ChoiceAnswer>(name);
    /// <summary>Gets a named boolean probability.</summary>
    public BooleanAnswer Boolean(string name) => Get<BooleanAnswer>(name);
    /// <summary>Gets a named probability using SystemOne terminology.</summary>
    public NoulAnswer Noul(string name) => new(Boolean(name).Probability);
    /// <summary>Gets a named ordered score.</summary>
    public ScoreAnswer Score(string name) => Get<ScoreAnswer>(name);
    /// <summary>Maps every label to an enum name; numeric and ambiguous aliases are refused.</summary>
    public ChoiceAnswer<T> Choice<T>(string name) where T : struct, Enum
    {
        ChoiceAnswer answer = Choice(name);
        Dictionary<string, T> names = Enum.GetNames<T>().ToDictionary(n => n, Enum.Parse<T>, StringComparer.OrdinalIgnoreCase);
        Dictionary<T, double> probabilities = [];
        foreach (KeyValuePair<string, double> pair in answer.Probabilities)
        {
            if (!names.TryGetValue(pair.Key, out T value) || !probabilities.TryAdd(value, pair.Value))
                throw new InvalidOperationException($"Choice '{name}' does not map uniquely to {typeof(T).Name}.");
        }
        return new ChoiceAnswer<T>(names[answer.Value], answer.Confidence, new ReadOnlyDictionary<T, double>(probabilities));
    }
    /// <summary>Gets an answer and checks its primitive.</summary>
    private T Get<T>(string name) where T : DecisionAnswer => Answers.TryGetValue(name, out DecisionAnswer? answer) && answer is T typed
        ? typed : throw new InvalidOperationException($"Decision answer '{name}' is missing or has a different type.");
}

/// <summary>A transport request. State is serialized as data, never interpolated into question text.</summary>
public sealed record DecisionRequest(string Model, object State, IReadOnlyDictionary<string, DecisionQuestion> Questions);

/// <summary>Per-call controls, including no-retry selection and model overrides.</summary>
public sealed record DecisionOptions
{
    /// <summary>Overrides the prompt's model profile.</summary>
    public string? Model { get; init; }
    /// <summary>Overall deadline, including queueing and retries.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Overrides the provider retry count; zero means one attempt.</summary>
    public int? RetryLimit { get; init; }
    /// <summary>Optional dynamic closed-set options by question ID. Only declared Choice questions may be replaced.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? Choices { get; init; }
}

/// <summary>Extension boundary for decision providers. No chat-completion dependency.</summary>
public interface IDecisionModelClient
{
    /// <summary>Evaluates a complete request; implementations must reject partial responses.</summary>
    Task<DecisionRun> EvaluateAsync(DecisionRequest request, DecisionOptions? options = null, CancellationToken token = default);
}

/// <summary>Batch-first named decision prompt execution.</summary>
public interface IDecisionService
{
    /// <summary>Evaluates all independent questions over one shared state.</summary>
    Task<DecisionRun> EvaluateAsync(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null);
    /// <summary>Evaluates a pack containing exactly one Choice question.</summary>
    Task<ChoiceAnswer<T>> ChoiceAsync<T>(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null) where T : struct, Enum;
    /// <summary>Evaluates a pack containing exactly one Boolean question.</summary>
    Task<BooleanAnswer> BooleanAsync(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null);
    /// <summary>Evaluates a pack containing exactly one Score question.</summary>
    Task<ScoreAnswer> ScoreAsync(string promptName, object state, CancellationToken token = default, DecisionOptions? options = null);
}

/// <summary>Checks request shape before sending any data to a provider.</summary>
internal static class DecisionValidation
{
    /// <summary>Validates primitive shapes and provider limits.</summary>
    internal static void Validate(DecisionRequest request, int maxChoices = 255, int maxScoreLevels = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        JsonNode? state = JsonSerializer.SerializeToNode(request.State);
        if (!IsTextStructure(state)) throw new ArgumentException("Decision state must be text, an object, or an array.");
        if (request.Questions.Count == 0) throw new ArgumentException("At least one decision question is required.");
        foreach (KeyValuePair<string, DecisionQuestion> pair in request.Questions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            DecisionQuestion q = pair.Value;
            if (!IsTextStructure(JsonSerializer.SerializeToNode(q.Instructions))) throw new ArgumentException("Instructions must be text, an object, or an array.");
            switch (q)
            {
                case ChoiceQuestion choice:
                    if (choice.Options.Count < 1 || choice.Options.Count > maxChoices || choice.Options.Keys.Any(string.IsNullOrWhiteSpace))
                        throw new ArgumentException("Choice option count or name is invalid.");
                    foreach (object? description in choice.Options.Values) ValidateDescription(description, true);
                    break;
                case BooleanQuestion boolean:
                    if (boolean.Meanings is not null)
                        foreach (KeyValuePair<string, object?> meaning in boolean.Meanings)
                        {
                            if (meaning.Key is not ("true" or "false")) throw new ArgumentException("Boolean criteria keys must be true or false.");
                            ValidateDescription(meaning.Value, false);
                        }
                    break;
                case ScoreQuestion score:
                    if (score.Levels.Count < 2 || score.Levels.Count > maxScoreLevels) throw new ArgumentException("Score needs two or more levels within model limits.");
                    foreach (object level in score.Levels) ValidateDescription(level, false);
                    break;
                default: throw new ArgumentException("Unsupported decision primitive.");
            }
        }
    }
    /// <summary>Checks a text/structure value.</summary>
    private static bool IsTextStructure(JsonNode? node) => node is JsonObject or JsonArray || node is JsonValue value && value.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text);
    /// <summary>Checks rubric entries without flattening structured data.</summary>
    private static void ValidateDescription(object? description, bool allowNull)
    {
        if (description is null && allowNull) return;
        if (!IsTextStructure(JsonSerializer.SerializeToNode(description))) throw new ArgumentException("Criteria descriptions must be text or structure.");
    }
}

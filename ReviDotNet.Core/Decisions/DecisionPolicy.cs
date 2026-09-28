namespace Revi;

/// <summary>Explicit outcomes of a calibrated decision policy.</summary>
public enum DecisionDisposition
{
    /// <summary>Meets the configured positive threshold.</summary>
    Accept,
    /// <summary>Meets the configured negative threshold.</summary>
    Reject,
    /// <summary>Insufficient confidence, unknown label, or mismatched calibration identity.</summary>
    Review
}

/// <summary>A separately versioned policy fitted for one question, model, question pack, and traffic slice.</summary>
public sealed class DecisionPolicy
{
    /// <summary>Stable policy identifier.</summary>
    [RConfigProperty("general_name")] public string Name { get; set; } = "";
    /// <summary>Policy version, independent of the question wording.</summary>
    [RConfigProperty("general_version")] public int Version { get; set; } = 1;
    /// <summary>Resolved model identifier used for calibration; aliases are not identities.</summary>
    [RConfigProperty("calibration_model")] public string Model { get; set; } = "";
    /// <summary>Effective question-pack hash used for calibration.</summary>
    [RConfigProperty("calibration_prompt-hash")] public string PromptHash { get; set; } = "";
    /// <summary>Dataset/traffic slice described by the calibration.</summary>
    [RConfigProperty("calibration_traffic-slice")] public string TrafficSlice { get; set; } = "";
    /// <summary>Question whose answer this policy interprets.</summary>
    [RConfigProperty("calibration_question")] public string Question { get; set; } = "";
    /// <summary>Minimum yes or selected-label probability for acceptance. No implicit default.</summary>
    [RConfigProperty("thresholds_accept")] public double? AcceptAt { get; set; }
    /// <summary>Maximum yes probability for rejection; intermediate probabilities require review.</summary>
    [RConfigProperty("thresholds_reject")] public double? RejectAt { get; set; }
    /// <summary>Labels that always require review.</summary>
    public IReadOnlySet<string> UnknownLabels { get; init; } = new HashSet<string>(["unknown", "other", "none"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Checks identity before interpreting an answer. Score thresholds need a task-specific policy.</summary>
    public DecisionDisposition Evaluate(DecisionRun run)
    {
        Validate();
        if (run.Model != Model || run.PromptHash != PromptHash || !run.Answers.TryGetValue(Question, out DecisionAnswer? answer))
            return DecisionDisposition.Review;
        if (answer is BooleanAnswer boolean)
        {
            if (!double.IsFinite(boolean.Probability) || boolean.Probability is < 0 or > 1) return DecisionDisposition.Review;
            return boolean.Probability >= AcceptAt ? DecisionDisposition.Accept : boolean.Probability <= RejectAt ? DecisionDisposition.Reject : DecisionDisposition.Review;
        }
        if (answer is ChoiceAnswer choice && !UnknownLabels.Contains(choice.Value) && choice.Probabilities.TryGetValue(choice.Value, out double probability))
            return probability >= AcceptAt ? DecisionDisposition.Accept : DecisionDisposition.Review;
        return DecisionDisposition.Review;
    }

    /// <summary>Refuses incomplete or invalid calibration specifications.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Version < 1 || string.IsNullOrWhiteSpace(Model) ||
            PromptHash.Length != 64 || !PromptHash.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(TrafficSlice) || string.IsNullOrWhiteSpace(Question) ||
            AcceptAt is null || RejectAt is null || !double.IsFinite(AcceptAt.Value) || !double.IsFinite(RejectAt.Value) ||
            RejectAt < 0 || AcceptAt > 1 || RejectAt >= AcceptAt)
            throw new InvalidOperationException("Decision policies require a version, model, prompt hash, traffic slice, question, and separated probability thresholds.");
    }

    /// <summary>Loads a policy from a standard human-readable RConfig file.</summary>
    public static DecisionPolicy Load(string path)
    {
        DecisionPolicy policy = RConfigParser.ToObject<DecisionPolicy>(RConfigParser.Read(path))!;
        policy.Validate();
        return policy;
    }
}

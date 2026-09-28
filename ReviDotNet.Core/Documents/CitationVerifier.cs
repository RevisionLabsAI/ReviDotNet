namespace Revi;

/// <summary>A citation check that distinguishes absent text from unsupported semantics.</summary>
public sealed record CitationCheck(bool QuotePresent, DecisionDisposition Disposition, string? Relation, DecisionRun? Decision);

/// <summary>Checks quote presence deterministically before asking a named decision pack about support.</summary>
public sealed class CitationVerifier(IDecisionService decisions)
{
    /// <summary>Verifies an exact quote in an original passage; a missing quote makes no model call.</summary>
    public async Task<CitationCheck> CheckAsync(DocumentPassage passage, string quote, string claim,
        DecisionPolicy policy, string prompt = "citation-support", DecisionOptions? options = null, CancellationToken token = default)
    {
        if (policy.Question != "relation") throw new ArgumentException("Citation policies must target the relation question.", nameof(policy));
        if (string.IsNullOrEmpty(quote) || !passage.Text.Contains(quote, StringComparison.Ordinal))
            return new(false, DecisionDisposition.Reject, "quote-not-found", null);
        DecisionRun run = await decisions.EvaluateAsync(prompt, new { claim, quote, passage = passage.Text }, token, options).ConfigureAwait(false);
        ChoiceAnswer answer = run.Choice("relation");
        DecisionDisposition disposition = policy.Evaluate(run);
        if (answer.Value != "supports" && disposition == DecisionDisposition.Accept) disposition = DecisionDisposition.Reject;
        return new(true, disposition, answer.Value, run);
    }
}

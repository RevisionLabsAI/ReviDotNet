namespace Revi.Refinery;

/// <summary>A labelled decision with a probability of its predicted event and an independent correctness label.</summary>
public sealed record DecisionCalibrationSample(string Id, string Model, string PromptHash, string TrafficSlice, double Probability, bool Correct);

/// <summary>Threshold metrics distinguish error among accepted predictions from error per incoming sample.</summary>
public sealed record DecisionThresholdMetrics(double Threshold, int Samples, int Accepted, int Errors,
    double Coverage, double? ConditionalError, double IncomingError);

/// <summary>Calibration and observed drift metrics for one frozen evaluation slice.</summary>
public sealed record DecisionCalibrationReport(int Samples, double BrierScore, double ExpectedCalibrationError,
    IReadOnlyList<DecisionThresholdMetrics> Thresholds, IReadOnlyList<double> ProbabilityHistogram);

/// <summary>Offline threshold analysis; does not claim conformal guarantees or automatically approve policies.</summary>
public static class DecisionCalibration
{
    /// <summary>Analyzes one model/question-pack/traffic-slice population. Use a held-out set for policy assessment.</summary>
    public static DecisionCalibrationReport Analyze(IReadOnlyList<DecisionCalibrationSample> samples, IEnumerable<double> thresholds)
    {
        if (samples.Count == 0) throw new ArgumentException("A labelled calibration set is required.");
        if (samples.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != samples.Count ||
            samples.Any(s => !double.IsFinite(s.Probability) || s.Probability is < 0 or > 1) ||
            samples.Select(s => (s.Model, s.PromptHash, s.TrafficSlice)).Distinct().Count() != 1)
            throw new ArgumentException("Samples must be unique, finite, and from one model, prompt, and traffic slice.");
        double[] histogram = new double[10];
        double ece = 0;
        for (int bin = 0; bin < 10; bin++)
        {
            DecisionCalibrationSample[] group = samples.Where(s => Math.Min(9, (int)(s.Probability * 10)) == bin).ToArray();
            histogram[bin] = (double)group.Length / samples.Count;
            if (group.Length > 0) ece += histogram[bin] * Math.Abs(group.Average(s => s.Probability) - group.Average(s => s.Correct ? 1d : 0));
        }
        List<DecisionThresholdMetrics> sweep = [];
        foreach (double threshold in thresholds.Distinct().Order())
        {
            if (!double.IsFinite(threshold) || threshold is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(thresholds));
            DecisionCalibrationSample[] accepted = samples.Where(s => s.Probability >= threshold).ToArray();
            int errors = accepted.Count(s => !s.Correct);
            sweep.Add(new(threshold, samples.Count, accepted.Length, errors, (double)accepted.Length / samples.Count,
                accepted.Length == 0 ? null : (double)errors / accepted.Length, (double)errors / samples.Count));
        }
        return new(samples.Count, samples.Average(s => Math.Pow(s.Probability - (s.Correct ? 1 : 0), 2)), ece, sweep, histogram);
    }

    /// <summary>Total variation between score histograms. A drift signal, not a correctness guarantee.</summary>
    public static double DistributionShift(DecisionCalibrationReport baseline, DecisionCalibrationReport current) =>
        baseline.ProbabilityHistogram.Zip(current.ProbabilityHistogram, (a, b) => Math.Abs(a - b)).Sum() / 2;
}

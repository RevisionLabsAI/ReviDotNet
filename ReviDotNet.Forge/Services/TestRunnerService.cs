// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;
using System.Threading.Channels;
using Revi;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Tracks the result of a single prompt test run.
/// </summary>
public class TestRunResult
{
    public required string PromptName { get; init; }
    public required string ModelName { get; init; }
    public required int RunNumber { get; init; }
    public required List<Input> Inputs { get; init; }
    public string Output { get; set; } = string.Empty;
    public TimeSpan? Ttft { get; set; }
    public TimeSpan TotalTime { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public AnalysisResult? Analysis { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Runs prompts against one or more models and streams results back via a channel.
/// </summary>
public class TestRunnerService
{
    private readonly IInferService _infer;
    private readonly IPromptManager _prompts;
    private readonly IModelManager _models;

    /// <summary>Initializes TestRunnerService with required inference and registry services.</summary>
    public TestRunnerService(IInferService infer, IPromptManager prompts, IModelManager models)
    {
        _infer = infer;
        _prompts = prompts;
        _models = models;
    }

    /// <summary>
    /// Runs a prompt against one or more models for a given number of runs.
    /// Results are pushed to the returned channel as they complete.
    /// </summary>
    public Channel<TestRunResult> RunTests(
        string promptName,
        IEnumerable<string> modelNames,
        List<Input> inputs,
        int runsPerModel,
        bool runAnalysis,
        CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<TestRunResult>();

        _ = Task.Run(async () =>
        {
            try
            {
                var tasks = new List<Task>();
                int runNumber = 0;

                foreach (string modelName in modelNames)
                {
                    ModelProfile? model = _models.Get(modelName);
                    if (model is null) continue;

                    for (int i = 0; i < runsPerModel; i++)
                    {
                        int capturedRun = ++runNumber;
                        string capturedModel = modelName;
                        ModelProfile capturedProfile = model;

                        tasks.Add(Task.Run(async () =>
                        {
                            var result = new TestRunResult
                            {
                                PromptName = promptName,
                                ModelName = capturedModel,
                                RunNumber = capturedRun,
                                Inputs = inputs
                            };

                            try
                            {
                                var sw = Stopwatch.StartNew();
                                var sb = new System.Text.StringBuilder();

                                await foreach (string token in _infer.CompletionStream(
                                    _prompts.Get(promptName)!, inputs,
                                    modelProfile: capturedProfile).WithCancellation(ct))
                                {
                                    if (result.Ttft is null)
                                        result.Ttft = sw.Elapsed;
                                    sb.Append(token);
                                }

                                sw.Stop();
                                result.Output = sb.ToString();
                                result.TotalTime = sw.Elapsed;
                                result.Success = true;

                                if (runAnalysis)
                                {
                                    result.Analysis = await AnalyzeAsync(
                                        promptName, capturedModel, inputs, result.Output, ct);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                result.ErrorMessage = "Cancelled";
                            }
                            catch (Exception ex)
                            {
                                result.ErrorMessage = ex.Message;
                                result.Success = false;
                            }

                            await channel.Writer.WriteAsync(result, ct);
                        }, ct));
                    }
                }

                await Task.WhenAll(tasks);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, ct);

        return channel;
    }

    private async Task<AnalysisResult?> AnalyzeAsync(
        string promptName,
        string modelName,
        List<Input> inputs,
        string response,
        CancellationToken ct)
    {
        try
        {
            var analysisInputs = new List<Input>
            {
                new("Prompt Name", promptName),
                new("Model", modelName),
                new("Inputs", string.Join(", ", inputs.Select(i => $"{i.Label}={i.Text}"))),
                new("Response", response)
            };

            return await _infer.ToObject<AnalysisResult>("Optimizer.Analyzer", analysisInputs);
        }
        catch
        {
            return null;
        }
    }
}

// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Revi;

namespace ReviDotNet.Optimizer;

/// <summary>
/// Main entry point for the ReviDotNet Optimizer.
/// </summary>
public class Program
{
    /// <summary>
    /// Entry point for the application.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    public static async Task Main(string[] args)
    {
        IHost host = CreateHostBuilder(args).Build();
        
        // Initialize Revi Managers (they are static)
        ProviderManager.Load(Assembly.GetExecutingAssembly());
        ModelManager.Load(Assembly.GetExecutingAssembly());
        PromptManager.Load(Assembly.GetExecutingAssembly());

        if (args.Length == 0)
        {
            PrintUsage();
            return;
        }

        string command = args[0].ToLower();
        switch (command)
        {
            case "run":
                await RunPrompt(host, args.Skip(1).ToArray());
                break;
            case "test":
                await RunTestSuite(host);
                break;
            default:
                Console.WriteLine($"Unknown command: {command}");
                PrintUsage();
                break;
        }
    }

    /// <summary>
    /// Creates the host builder for dependency injection.
    /// </summary>
    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: true);
                config.AddEnvironmentVariables();
            })
            .ConfigureServices((hostContext, services) =>
            {
                // Register ReviDotNet services
                services.AddSingleton<IRlogEventPublisher, NullRlogEventPublisher>();
                services.AddSingleton<IReviLogger, ReviLogger>();
                services.AddSingleton(typeof(IReviLogger<>), typeof(ReviLogger<>));
            });

    /// <summary>
    /// Prints usage instructions to the console.
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine("ReviDotNet Optimizer Usage:");
        Console.WriteLine("  run <prompt-name> [input-key=input-value ...]  - Runs a specific prompt.");
        Console.WriteLine("  test                                           - Runs the full test suite.");
    }

    /// <summary>
    /// Runs an individual prompt and displays its output and performance metrics.
    /// </summary>
    private static async Task RunPrompt(IHost host, string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: run <prompt-name> [input-key=input-value ...]");
            return;
        }

        string promptName = args[0];
        List<Input> inputs = [];
        for (int i = 1; i < args.Length; i++)
        {
            string[] parts = args[i].Split('=');
            if (parts.Length == 2)
            {
                inputs.Add(new Input(parts[0], parts[1]));
            }
        }

        Prompt? prompt = PromptManager.Get(promptName);
        if (prompt == null)
        {
            Console.WriteLine($"Prompt '{promptName}' not found.");
            return;
        }

        Console.WriteLine($"Running prompt: {prompt.Name}");
        
        Stopwatch sw = Stopwatch.StartNew();
        TimeSpan? ttft = null;
        string fullResponse = "";

        await foreach (string token in Infer.CompletionStream(prompt, inputs))
        {
            if (ttft == null)
            {
                ttft = sw.Elapsed;
            }
            Console.Write(token);
            fullResponse += token;
        }
        sw.Stop();

        Console.WriteLine("\n" + new string('-', 20));
        Console.WriteLine($"TTFT: {ttft?.TotalMilliseconds:F2}ms");
        Console.WriteLine($"Total Time: {sw.Elapsed.TotalMilliseconds:F2}ms");
        Console.WriteLine(new string('-', 20));
    }

    /// <summary>
    /// Runs the test suite against all models and prompts, including inference-based analysis.
    /// </summary>
    private static async Task RunTestSuite(IHost host)
    {
        List<ModelProfile> models = ModelManager.GetAll().Where(m => m.Enabled).ToList();
        // For this example, we'll just test the Optimizer.SimpleTask prompt if it exists
        Prompt? testPrompt = PromptManager.Get("Optimizer.SimpleTask");
        
        if (testPrompt == null)
        {
            Console.WriteLine("Test prompt 'Optimizer.SimpleTask' not found. Ensure it exists in RConfigs/Prompts.");
            return;
        }

        List<Input> inputs = [new Input("Task", "Explain the concept of 'Time to First Token' in LLMs.")];

        Console.WriteLine("Starting Test Suite...");
        Console.WriteLine($"Testing Prompt: {testPrompt.Name}");
        Console.WriteLine();

        foreach (ModelProfile model in models)
        {
            Console.WriteLine($"[Model: {model.Name}]");
            
            Stopwatch sw = Stopwatch.StartNew();
            TimeSpan? ttft = null;
            string responseText = "";

            try
            {
                await foreach (string token in Infer.CompletionStream(testPrompt, inputs, modelProfile: model))
                {
                    if (ttft == null)
                    {
                        ttft = sw.Elapsed;
                    }
                    responseText += token;
                }
                sw.Stop();

                Console.WriteLine($"- TTFT: {ttft?.TotalMilliseconds:F2}ms");
                Console.WriteLine($"- Total Time: {sw.Elapsed.TotalMilliseconds:F2}ms");
                
                // Perform Inference-provided Analysis
                await RunAnalysis(host, testPrompt.Name ?? "Unknown", model.Name ?? "Unknown", inputs, responseText);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"- Error: {ex.Message}");
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Runs the analysis prompt to evaluate the output of a prompt execution.
    /// </summary>
    private static async Task RunAnalysis(IHost host, string promptName, string modelName, List<Input> inputs, string response)
    {
        Console.WriteLine("- Performing Inference Analysis...");
        
        List<Input> analysisInputs = 
        [
            new Input("Prompt Name", promptName),
            new Input("Model", modelName),
            new Input("Inputs", string.Join(", ", inputs.Select(i => $"{i.Label}={i.Text}"))),
            new Input("Response", response)
        ];

        try
        {
            // Use gpt-4o-mini (or default) for analysis
            AnalysisResult? analysis = await Infer.ToObject<AnalysisResult>("Optimizer.Analyzer", analysisInputs);
            
            if (analysis != null)
            {
                Console.WriteLine($"  * Fulfilled Request: {analysis.FulfilledRequest}");
                Console.WriteLine($"  * Quality Score: {analysis.QualityScore}/10");
                Console.WriteLine($"  * Analysis: {analysis.Analysis}");
                Console.WriteLine($"  * Improvements: {analysis.Improvements}");
            }
            else
            {
                Console.WriteLine("  * Analysis failed to return a result.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  * Analysis Error: {ex.Message}");
        }
    }
}

/// <summary>
/// Data structure for the inference-based analysis result.
/// </summary>
public class AnalysisResult
{
    /// <summary>
    /// Gets or sets whether the request was adequately fulfilled.
    /// </summary>
    public bool FulfilledRequest { get; set; }

    /// <summary>
    /// Gets or sets the quality score (1-10).
    /// </summary>
    public int QualityScore { get; set; }

    /// <summary>
    /// Gets or sets the detailed analysis text.
    /// </summary>
    public string Analysis { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets suggested improvements.
    /// </summary>
    public string Improvements { get; set; } = string.Empty;
}

/// <summary>
/// A null implementation of IRlogEventPublisher that does nothing.
/// </summary>
public class NullRlogEventPublisher : IRlogEventPublisher
{
    /// <inheritdoc />
    public Task PublishLogEventAsync(RlogEvent rlogEvent) => Task.CompletedTask;

    /// <inheritdoc />
    public void PublishLogEvent(RlogEvent rlogEvent) { }
}

# Prompt Optimizer & Test Runner (ReviDotNet.Forge)

Prompt optimization and testing in ReviDotNet ship as part of the **ReviDotNet.Forge** Blazor web app — there is no standalone `ReviDotNet.Optimizer` console application. The functionality is exposed through two pages and the services that back them:

| Page | Route | Backing service |
| :--- | :--- | :--- |
| Optimize | `/optimize` (`Components/Pages/Optimize/Optimize.razor`) | `OptimizerService` |
| Test | `/test` (`Components/Pages/Test/Test.razor`) | `TestRunnerService` |

It lets you run a prompt across one or more models, capture performance metrics (Time to First Token and total time), and get an AI-powered qualitative analysis of each response.

## Setup

### Prerequisites

- .NET 9.0 SDK
- API keys for the providers you want to exercise (e.g., OpenAI)

### Environment variables

Provider API keys are loaded from environment variables following the pattern `PROVAPIKEY__<PROVIDER_NAME>` (uppercase; spaces/hyphens become underscores):

- `PROVAPIKEY__OPENAI` — OpenAI
- `PROVAPIKEY__CLAUDE` — Anthropic (Claude)
- `PROVAPIKEY__GEMINI` — Google (Gemini)

### Configuration

Forge loads its `RConfigs` as embedded resources (see the note on embedded-only Forge configs). They live under `ReviDotNet.Forge/RConfigs`:

- `RConfigs/Providers` — provider configs (e.g. `openai.rcfg`, `claude.rcfg`, `gemini.rcfg`)
- `RConfigs/Models` — model profiles
- `RConfigs/Prompts` — prompt templates, including the optimizer prompts under `RConfigs/Prompts/Optimizer`

## Usage

Run the Forge web app and use the UI:

```bash
dotnet run --project ReviDotNet.Forge
```

Then open the app and navigate to:

- **`/test`** — pick a prompt, select one or more enabled models, supply inputs, choose the number of runs per model, and (optionally) enable qualitative analysis. Results stream in as each run completes.
- **`/optimize`** — review a prompt's output and the analyzer's qualitative feedback / suggested improvements.

### Driving it from code

The same capabilities are available through the services (both registered in Forge's DI):

```csharp
// Qualitative analysis of a single response (uses the Optimizer.Analyzer prompt internally).
AnalysisResult? analysis = await optimizerService.AnalyzeAsync(
    promptName: "Optimizer.SimpleTask",
    modelName:  "gpt-4o-mini",
    inputs:     [ new Input("Task", "Write a short poem about coding.") ],
    response:   modelOutput);

// Run a prompt across several models, N runs each, streaming TestRunResult items as they finish.
Channel<TestRunResult> results = testRunnerService.RunTests(
    promptName:  "Optimizer.SimpleTask",
    modelNames:  ["gpt-4o-mini", "claude-3-5-sonnet"],
    inputs:      [ new Input("Task", "Write a short poem about coding.") ],
    runsPerModel: 3,
    runAnalysis:  true);

await foreach (TestRunResult r in results.Reader.ReadAllAsync())
{
    // r carries timing (TTFT / total) and, when runAnalysis is true, the AnalysisResult.
}
```

## Performance metrics

- **TTFT (Time to First Token)** — duration between sending the request and receiving the first response chunk.
- **Total Time** — duration from request start to completion.

## Qualitative analysis

When analysis is enabled, each response is scored by the `Optimizer.Analyzer` prompt, which evaluates:

- **Fulfilled Request** — whether the response adequately addressed the instructions.
- **Quality Score** — a 1–10 rating of response quality.
- **Detailed Analysis** — strengths and weaknesses of the response.
- **Improvements** — suggestions for prompt or parameter tuning.

> Note: the `ReviDotNet.Core/Optimization` types (`Optimization`, `Evaluation`, `PromptEvalTicket`, `TestTicket`) are an earlier, largely-stubbed experiment and are **not** what drives this feature. The working implementation is the Forge `OptimizerService` / `TestRunnerService` described above.

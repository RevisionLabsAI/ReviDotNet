# ReviDotNet.Optimizer

The ReviDotNet Optimizer is a console application designed to test, analyze, and optimize AI prompts using the ReviDotNet framework. It provides performance metrics such as Time to First Token (TTFT) and total execution time, as well as AI-powered qualitative analysis of the responses.

## Setup

### Prerequisites

- .NET 9.0 SDK
- API keys for supported providers (e.g., OpenAI)

### Environment Variables

The Optimizer uses environment variables to securely load API keys for different providers. Ensure you have the following environment variables set as needed:

- `PROVAPIKEY__OPENAI`: Your OpenAI API key.
- `PROVAPIKEY__CLAUDE`: Your Anthropic (Claude) API key.
- `PROVAPIKEY__GEMINI`: Your Google (Gemini) API key.

Note: The environment variable name follows the pattern `PROVAPIKEY__<PROVIDER_NAME>` where `<PROVIDER_NAME>` is uppercase and spaces/hyphens are replaced by underscores.

### Configuration

RConfigs are located in the `RConfigs` directory:
- `RConfigs/Providers`: Provider configurations (e.g., `openai.rcfg`, `claude.rcfg`, `gemini.rcfg`).
- `RConfigs/Models`: Model profiles (e.g., `gpt-4o-mini.rcfg`, `claude-3-5-sonnet.rcfg`, `gemini-1-5-flash.rcfg`).
- `RConfigs/Prompts`: Prompt templates (e.g., `Optimizer/SimpleTask.pmt`).

## Usage

Run the Optimizer from the command line:

```bash
dotnet run --project ReviDotNet.Optimizer <command> [args]
```

### Commands

#### 1. `run`

Executes a specific prompt and displays the output along with performance metrics.

```bash
dotnet run --project ReviDotNet.Optimizer run Optimizer.SimpleTask Task="Write a short poem about coding."
```

#### 2. `test`

Runs a test suite across all enabled models using a set of test prompts. It performs both quantitative (timing) and qualitative (AI-powered) analysis.

```bash
dotnet run --project ReviDotNet.Optimizer test
```

## Performance Metrics

- **TTFT (Time to First Token)**: The duration between sending the request and receiving the first chunk of the response.
- **Total Time**: The total duration from request start to completion.

## Qualitative Analysis

The Optimizer includes a special `Optimizer.Analyzer` prompt that evaluates the output of other prompts based on:
- **Fulfilled Request**: Whether the response adequately addressed the instructions.
- **Quality Score**: A 1-10 rating of the response quality.
- **Detailed Analysis**: A breakdown of the response's strengths and weaknesses.
- **Improvements**: Suggestions for prompt or parameter tuning.

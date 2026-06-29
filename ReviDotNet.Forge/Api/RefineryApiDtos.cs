// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using ReviDotNet.Forge.Services;

namespace ReviDotNet.Forge.Api;

/// <summary>One key/value input pair for an optimize run (mirrors a prompt's <c>[Label] value</c> input).</summary>
public sealed record OptimizeInputDto(string Key, string Value);

/// <summary>
/// Request body for <c>POST /api/refinery/optimize</c>: run a prompt across one or more models, analyse each
/// result, aggregate the analyses into suggestions, then produce a revised prompt.
/// </summary>
public sealed record OptimizeRequest(
    string PromptName,
    string[] ModelNames,
    OptimizeInputDto[]? Inputs = null,
    int? RunsPerModel = null,
    int? MaxSuggestions = null);

/// <summary>
/// Response for <c>POST /api/refinery/optimize</c>: the ranked suggestions plus the buffered revised prompt
/// (the full <c>.pmt</c> content produced by the Optimizer's reviser stream).
/// </summary>
public sealed record OptimizeResponse(
    IReadOnlyList<PromptSuggestion> Suggestions,
    string RevisedPromptContent);

/// <summary>
/// Request body for <c>POST /api/refinery/test/run</c>: run a saved suite by name. <see cref="AgentName"/>
/// (optional) overrides the suite's own agent, forcing agent-mode against that agent.
/// </summary>
public sealed record TestRunRequest(
    string SuiteName,
    string? AgentName = null);

/// <summary>
/// Request body for <c>POST /api/refinery/generate-scenarios</c>: author up to <see cref="Count"/> fresh
/// evaluation scenarios for an agent in a target category.
/// </summary>
public sealed record GenerateScenariosRequest(
    string AgentName,
    string AgentSpecSection,
    string TargetCategory,
    int? Count = null);

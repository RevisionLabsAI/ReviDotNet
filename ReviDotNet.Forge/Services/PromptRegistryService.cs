// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Text;
using Microsoft.Extensions.Configuration;
using Revi;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Provides access to the Revi prompt registry with editing and persistence support.
/// </summary>
public class PromptRegistryService
{
    private readonly string _promptsSourcePath;
    private readonly IPromptManager _prompts;

    /// <summary>Initialises the service with configuration and the DI prompt registry.</summary>
    public PromptRegistryService(IConfiguration configuration, IPromptManager prompts)
    {
        _promptsSourcePath = configuration["Forge:PromptsSourcePath"] ?? "RConfigs/Prompts";
        _prompts = prompts;
    }

    /// <summary>
    /// Returns all loaded prompts.
    /// </summary>
    public List<Prompt> GetAll()
    {
        return _prompts.GetAll();
    }

    /// <summary>
    /// Returns a single prompt by name.
    /// </summary>
    public Prompt? GetByName(string name)
    {
        return _prompts.Get(name);
    }

    /// <summary>
    /// Saves a prompt back to disk and updates the in-memory registry.
    /// The file is written relative to the configured prompts source path,
    /// using the prompt name to derive the file path (dots become directory separators).
    /// </summary>
    public void Save(Prompt prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt.Name))
            throw new InvalidOperationException("Prompt must have a name.");

        string relativePath = prompt.Name!.Replace('.', Path.DirectorySeparatorChar) + ".pmt";
        string fullPath = Path.Combine(_promptsSourcePath, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, SerializePrompt(prompt), Encoding.UTF8);

        // Reload the prompt into the in-memory registry
        _prompts.LoadFromFile(fullPath);
    }

    /// <summary>
    /// Writes a new .pmt file to the source path and loads it into the registry.
    /// </summary>
    public void SaveNew(string promptName, string content)
    {
        string relativePath = promptName.Replace('.', Path.DirectorySeparatorChar) + ".pmt";
        string fullPath = Path.Combine(_promptsSourcePath, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content, Encoding.UTF8);

        _prompts.LoadFromFile(fullPath);
    }

    /// <summary>
    /// Converts a GuidanceSchemaType enum to its .pmt file string representation.
    /// Returns null for Disabled/Default (not emitted).
    /// </summary>
    public static string? ToGuidanceSchemaString(GuidanceSchemaType? schema) => schema switch
    {
        GuidanceSchemaType.JsonAuto => "json-auto",
        GuidanceSchemaType.JsonManual => "json",
        GuidanceSchemaType.RegexAuto => "regex-auto",
        GuidanceSchemaType.RegexManual => "regex",
        GuidanceSchemaType.GNBFAuto => "gbnf-auto",
        GuidanceSchemaType.GNBFManual => "gbnf",
        _ => null
    };

    /// <summary>
    /// Serializes a Prompt to .pmt file format.
    /// </summary>
    public static string SerializePrompt(Prompt prompt)
    {
        var sb = new StringBuilder();

        sb.AppendLine("[[information]]");
        sb.AppendLine($"name = {prompt.Name}");
        if (prompt.Version.HasValue)
            sb.AppendLine($"version = {prompt.Version}");
        sb.AppendLine();

        // Settings section — only emit non-default values
        string? guidanceSchemaStr = ToGuidanceSchemaString(prompt.GuidanceSchema);
        bool hasSettings = prompt.RequestJson == true || guidanceSchemaStr is not null ||
                           prompt.ChainOfThought == true || prompt.FewShotExamples.HasValue ||
                           prompt.MaxTokens.HasValue || prompt.Timeout.HasValue ||
                           prompt.RequireValidOutput == true;
        if (hasSettings)
        {
            sb.AppendLine("[[settings]]");
            if (prompt.RequestJson == true) sb.AppendLine("request-json = true");
            if (guidanceSchemaStr is not null)
                sb.AppendLine($"guidance-schema-type = {guidanceSchemaStr}");
            if (prompt.ChainOfThought == true) sb.AppendLine("chain-of-thought = true");
            if (prompt.FewShotExamples.HasValue) sb.AppendLine($"few-shot-examples = {prompt.FewShotExamples}");
            if (prompt.MaxTokens.HasValue) sb.AppendLine($"max-tokens = {prompt.MaxTokens}");
            if (prompt.Timeout.HasValue) sb.AppendLine($"timeout = {prompt.Timeout}");
            if (prompt.RequireValidOutput == true) sb.AppendLine("require-valid-output = true");
            sb.AppendLine();
        }

        // Tuning section — only emit if non-default
        bool hasTuning = prompt.Temperature.HasValue || prompt.TopK.HasValue || prompt.TopP.HasValue ||
                         prompt.MinP.HasValue || prompt.PresencePenalty.HasValue ||
                         prompt.FrequencyPenalty.HasValue || prompt.RepetitionPenalty.HasValue;
        if (hasTuning)
        {
            sb.AppendLine("[[tuning]]");
            if (prompt.Temperature.HasValue) sb.AppendLine($"temperature = {prompt.Temperature}");
            if (prompt.TopK.HasValue) sb.AppendLine($"top-k = {prompt.TopK}");
            if (prompt.TopP.HasValue) sb.AppendLine($"top-p = {prompt.TopP}");
            if (prompt.MinP.HasValue) sb.AppendLine($"min-p = {prompt.MinP}");
            if (prompt.PresencePenalty.HasValue) sb.AppendLine($"presence-penalty = {prompt.PresencePenalty}");
            if (prompt.FrequencyPenalty.HasValue) sb.AppendLine($"frequency-penalty = {prompt.FrequencyPenalty}");
            if (prompt.RepetitionPenalty.HasValue) sb.AppendLine($"repetition-penalty = {prompt.RepetitionPenalty}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(prompt.System))
        {
            sb.AppendLine("[[_system]]");
            sb.AppendLine(prompt.System!.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(prompt.Instruction))
        {
            sb.AppendLine("[[_instruction]]");
            sb.AppendLine(prompt.Instruction!.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(prompt.Schema))
        {
            sb.AppendLine("[[_schema]]");
            sb.AppendLine(prompt.Schema!.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

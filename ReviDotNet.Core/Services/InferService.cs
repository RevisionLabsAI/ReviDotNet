// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Collections;
using System.Data;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Revi;

/// <summary>
/// DI service implementation of <see cref="IInferService"/>. Wraps LLM completion logic that was
/// previously on the static <c>Infer</c> class, using injected registry services instead of
/// static manager calls.
/// </summary>
public sealed class InferService(
    IPromptManager prompts,
    IModelManager models,
    IProviderManager providers,
    IReviLogger<InferService> logger) : IInferService
{
    // ===================
    //  Inference Calling
    // ===================

    #region Completion Inference

    /// <inheritdoc/>
    public async Task<CompletionResult?> Completion(
        Prompt prompt,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        Type? outputType = null,
        CancellationToken token = default,
        bool directRoute = false)
    {
        if (ForgeManager.IsConfigured && ForgeManager.Client is not null && !directRoute)
            return await ForgeManager.Client.GenerateAsync(prompt, inputs, token);

        CompletionResult? result = null;
        string promptString;
        List<Message> messages;

        ModelProfile foundModel = FindModel(prompt, modelProfile, modelName);

        if (await FilterCheck(prompt, inputs, token))
            throw new SecurityException("FilterCheck failed!");

        if (!Enum.TryParse(prompt.CompletionType, out CompletionType type))
            throw new Exception($"Invalid completion type: '{prompt.CompletionType}'");

        DateTime startTime = DateTime.UtcNow;
        bool inferenceSuccess = false;
        try
        {
            switch (type)
            {
                case CompletionType.ChatOnly:
                    messages = CompletionChat.BuildMessages(prompt, foundModel, inputs);
                    result = await CallInference(messages, prompt, foundModel, outputType, token);
                    break;

                case CompletionType.PromptOnly:
                    promptString = CompletionPrompt.BuildString(prompt, foundModel, inputs);
                    result = await CallInference(promptString, prompt, foundModel, outputType, token);
                    break;

                case CompletionType.PromptChatOne:
                case CompletionType.PromptChatMulti:
                {
                    if (foundModel.Provider.SupportsCompletion ?? false)
                    {
                        promptString = CompletionPrompt.BuildString(prompt, foundModel, inputs);
                        result = await CallInference(promptString, prompt, foundModel, outputType, token);
                    }
                    else
                    {
                        messages = CompletionChat.BuildMessages(prompt, foundModel, inputs);
                        result = await CallInference(messages, prompt, foundModel, outputType, token);
                    }
                    break;
                }

                default:
                    throw new Exception($"Unexpected completion type: '{prompt.CompletionType}'");
            }

            inferenceSuccess = result is not null;
        }
        finally
        {
            if (directRoute && ForgeManager.Reporter is not null)
            {
                ForgeManager.Reporter.ReportAndForget(new ForgeDirectUsageReport
                {
                    ClientId = ForgeManager.Config?.ClientId ?? "unknown",
                    PromptName = prompt.Name,
                    ModelName = foundModel.Name,
                    ProviderName = foundModel.Provider?.Name ?? string.Empty,
                    Success = inferenceSuccess,
                    InputTokens = result?.InputTokens ?? 0,
                    OutputTokens = result?.OutputTokens ?? 0,
                    LatencyMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                    WasStreaming = false
                });
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<CompletionResult?> Completion(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        Type? outputType = null,
        CancellationToken token = default)
    {
        return await Completion(
            FindPrompt(promptName),
            inputs,
            modelProfile,
            modelName,
            outputType,
            token);
    }

    #endregion

    #region Streaming Inference

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> CompletionStream(
        Prompt prompt,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        Type? outputType = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        bool directRoute = false)
    {
        if (ForgeManager.IsConfigured && ForgeManager.Client is not null && !directRoute)
        {
            await foreach (string chunk in ForgeManager.Client.GenerateStreamAsync(prompt, inputs, cancellationToken))
                yield return chunk;
            yield break;
        }

        ModelProfile foundModel = FindModel(prompt, modelProfile, modelName);

        if (await FilterCheck(prompt, inputs))
            throw new SecurityException("FilterCheck failed!");

        if (!Enum.TryParse(prompt.CompletionType, out CompletionType type))
            throw new Exception($"Invalid completion type: '{prompt.CompletionType}'");

        IAsyncEnumerable<string> streamResult;

        switch (type)
        {
            case CompletionType.ChatOnly:
                List<Message> msgs = CompletionChat.BuildMessages(prompt, foundModel, inputs);
                streamResult = CallStreamingInference(msgs, prompt, foundModel, outputType, cancellationToken);
                break;

            case CompletionType.PromptOnly:
                string promptStr = CompletionPrompt.BuildString(prompt, foundModel, inputs);
                streamResult = CallStreamingInference(promptStr, prompt, foundModel, outputType, cancellationToken);
                break;

            case CompletionType.PromptChatOne:
            case CompletionType.PromptChatMulti:
            {
                if (foundModel.Provider.SupportsCompletion ?? false)
                {
                    string ps = CompletionPrompt.BuildString(prompt, foundModel, inputs);
                    streamResult = CallStreamingInference(ps, prompt, foundModel, outputType, cancellationToken);
                }
                else
                {
                    List<Message> ms = CompletionChat.BuildMessages(prompt, foundModel, inputs);
                    streamResult = CallStreamingInference(ms, prompt, foundModel, outputType, cancellationToken);
                }
                break;
            }

            default:
                throw new Exception($"Unexpected completion type: '{prompt.CompletionType}'");
        }

        bool reportToForge = directRoute && ForgeManager.Reporter is not null;
        StringBuilder? outputAccumulator = reportToForge ? new StringBuilder() : null;
        DateTime streamStart = DateTime.UtcNow;
        bool streamedSuccessfully = false;

        try
        {
            await foreach (string chunk in streamResult)
            {
                outputAccumulator?.Append(chunk);
                yield return chunk;
            }
            streamedSuccessfully = true;
        }
        finally
        {
            if (reportToForge && ForgeManager.Reporter is not null)
            {
                string inputText = (prompt.System ?? "") + (prompt.Instruction ?? "")
                                   + string.Join("", inputs?.Select(i => i.Text) ?? []);
                ForgeManager.Reporter.ReportAndForget(new ForgeDirectUsageReport
                {
                    ClientId = ForgeManager.Config?.ClientId ?? "unknown",
                    PromptName = prompt.Name,
                    ModelName = foundModel.Name,
                    ProviderName = foundModel.Provider?.Name ?? string.Empty,
                    Success = streamedSuccessfully,
                    InputTokens = Util.EstTokenCountFromCharCount(inputText.Length),
                    OutputTokens = Util.EstTokenCountFromCharCount(outputAccumulator?.Length ?? 0),
                    LatencyMs = (long)(DateTime.UtcNow - streamStart).TotalMilliseconds,
                    WasStreaming = true
                });
            }
        }
    }

    #endregion


    // ===================
    //  Object Converters
    // ===================

    #region Object Converters

    /// <inheritdoc/>
    public async Task<T?> ToObject<T>(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        int retryAttempt = 0,
        int? originalRetryLimit = null,
        CancellationToken token = default)
    {
        Type outputType = typeof(T);
        T? newObject = default;
        string? extractedJson = null;

        Prompt prompt = FindPrompt(promptName);

        if (prompt.RequestJson is false)
            throw new Exception($"InferService.ToObject: RequestJson is false for prompt '{prompt.Name}'");

        CompletionResult? result = await Completion(prompt, inputs, modelProfile, modelName, outputType, token);

        try
        {
            extractedJson = Util.ExtractJson(result?.Selected, prompt.ChainOfThought);

            if (string.IsNullOrEmpty(extractedJson))
            {
                string serializedResult = JsonConvert.SerializeObject(result, Formatting.Indented);
                throw new NoNullAllowedException(
                    $"InferToObject(): json was null or empty: \n'''\n{serializedResult}\n'''\n");
            }

            JsonSerializerSettings settings = new()
                { Converters = new List<JsonConverter> { new StringEnumConverter() } };
            newObject = JsonConvert.DeserializeObject<T>(extractedJson, settings);
        }
        catch (Exception e)
        {
            if (string.IsNullOrEmpty(extractedJson))
            {
                Util.Log($"WARNING: Missing JSON from InferService.ToObject output: \n{e.Message}\n");
                return default;
            }

            Util.Log($"WARNING: Caught faulty JSON output:\n'''{extractedJson}\n'''");
            string dump = $"Faulty JSON!\nPrompt: {prompt.Name}\nFull Prompt: {result?.FullPrompt}\nOutput: {result?.Selected}";
            await Util.DumpLog(dump, "faultyjson");

            result = await Completion(
                FindPrompt("json-fixer"),
                [
                    new Input("Schema", Util.JsonStringFromType(outputType)),
                    new Input("Bad JSON", extractedJson)
                ],
                null,
                null,
                outputType,
                token);

            extractedJson = Util.ExtractJson(result?.Selected, prompt.ChainOfThought);

            try
            {
                if (!string.IsNullOrEmpty(extractedJson))
                {
                    JsonSerializerSettings settings = new()
                        { Converters = new List<JsonConverter> { new StringEnumConverter() } };
                    newObject = JsonConvert.DeserializeObject<T>(extractedJson, settings);
                }
            }
            catch
            {
                Util.Log($"JSON remediation FAILED with output:\n {extractedJson}\n\n");
            }
            finally
            {
                Util.Log($"JSON remediation {(newObject is not null ? "SUCCEEDED!" : "FAILED")}");
            }
        }

        T? castObject = (T?)Convert.ChangeType(newObject, typeof(T));
        if (ValidateObject<T>(castObject, prompt))
            return castObject;

        Util.Log($"InferService.ToObject() object was invalid for prompt '{prompt.Name}'");

        if (originalRetryLimit is null)
            originalRetryLimit = prompt.RetryAttempts;

        if (retryAttempt < (originalRetryLimit ?? 0))
        {
            Util.Log($"Retrying InferService.ToObject() for prompt '{prompt.Name}'");

            string promptToRetry = promptName;
            if (prompt.RetryPrompt is not null)
                promptToRetry = prompt.RetryPrompt;

            return await ToObject<T>(promptToRetry, inputs, modelProfile, modelName, retryAttempt + 1, originalRetryLimit, token);
        }

        return castObject;
    }

    /// <inheritdoc/>
    public async Task<T?> ToObject<T>(
        string promptName,
        Input? input,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default)
    {
        List<Input>? inputs = input is not null ? [input] : null;
        return await ToObject<T>(promptName, inputs, modelProfile, modelName, token: token);
    }

    /// <inheritdoc/>
    public async Task<JObject?> ToJObject(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default)
    {
        JObject? newObject = null;
        try
        {
            string? output = await ToString(promptName, inputs, modelProfile, modelName, token);
            if (output is not null)
                newObject = JObject.Parse(output);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
        return newObject;
    }

    /// <inheritdoc/>
    public async Task<JObject?> ToJObject(
        string promptName,
        Input? input = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default)
    {
        List<Input>? inputs = input is not null ? [input] : null;
        return await ToJObject(promptName, inputs, modelProfile, modelName, token);
    }

    /// <inheritdoc/>
    public async Task<bool?> ToBool(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default)
    {
        string? result = await ToString(promptName, inputs, modelProfile, modelName, token);
        return result?.ToLower() switch
        {
            "true" => true,
            "false" => false,
            _ => null
        };
    }

    /// <inheritdoc/>
    public async Task<bool?> ToBool(
        string promptName,
        Input? input = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default)
    {
        List<Input>? inputs = input is not null ? [input] : null;
        return await ToBool(promptName, inputs, modelProfile, modelName, token);
    }

    #endregion


    // ===================
    //  Enum Converters
    // ===================

    #region Enum Converters

    /// <inheritdoc/>
    public async Task<TEnum> ToEnum<TEnum>(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        bool includeEnumValues = false,
        int retryAttempt = 0,
        int? originalRetryLimit = null,
        CancellationToken token = default) where TEnum : struct, Enum
    {
        Type enumType = typeof(TEnum);
        TEnum parsedValue = default;
        string? rawOutput = null;

        Prompt prompt = FindPrompt(promptName);

        if (includeEnumValues)
        {
            inputs ??= [];
            if (!inputs.Any(i => string.Equals(i.Label, "Enum Values", StringComparison.OrdinalIgnoreCase)))
                inputs.Add(new Input("Enum Values", Util.EnumNamesToString(enumType)));
        }

        CompletionResult? result = await Completion(prompt, inputs, modelProfile, modelName, null, token);
        rawOutput = result?.Selected;

        if (TryParseEnum(rawOutput, out parsedValue))
            return parsedValue;

        try
        {
            Prompt? fixer = prompts.Get("enum-fixer");
            if (fixer is not null)
            {
                List<Input> fixInputs =
                [
                    new Input("Enum Values", Util.EnumNamesToString(enumType)),
                    new Input("Bad Output", rawOutput ?? string.Empty),
                    new Input("Instruction", "Convert the input into exactly one of the enum names and output ONLY that enum name.")
                ];
                CompletionResult? fixResult = await Completion(fixer, fixInputs, modelProfile, modelName, null, token);
                if (TryParseEnum(fixResult?.Selected, out parsedValue))
                    return parsedValue;
            }
        }
        catch (Exception e)
        {
            Util.Log($"Enum remediation attempt failed: {e.Message}");
        }

        if (originalRetryLimit is null)
            originalRetryLimit = prompt.RetryAttempts;

        if (retryAttempt < (originalRetryLimit ?? 0))
        {
            Util.Log($"Retrying InferService.ToEnum() for prompt '{prompt.Name}'");

            string promptToRetry = promptName;
            if (prompt.RetryPrompt is not null)
                promptToRetry = prompt.RetryPrompt;

            return await ToEnum<TEnum>(promptToRetry, inputs, modelProfile, modelName, includeEnumValues, retryAttempt + 1, originalRetryLimit, token);
        }

        return parsedValue;
    }

    /// <inheritdoc/>
    public async Task<TEnum> ToEnum<TEnum>(
        string promptName,
        Input? input,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        bool includeEnumValues = false,
        CancellationToken token = default) where TEnum : struct, Enum
    {
        List<Input>? inputs = input is not null ? [input] : null;
        return await ToEnum<TEnum>(promptName, inputs, modelProfile, modelName, includeEnumValues, token: token);
    }

    #endregion


    // =======================
    //  Convenience Overloads
    // =======================

    #region String Overloads

    /// <inheritdoc/>
    public async Task<string?> ToString(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default)
    {
        CompletionResult? result = await Completion(promptName, inputs, modelProfile, modelName, token: token);
        return result?.Selected;
    }

    /// <inheritdoc/>
    public async Task<string?> ToString(
        string promptName,
        Input? input,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default)
    {
        List<Input>? inputs = input is not null ? [input] : null;
        return await ToString(promptName, inputs, modelProfile, modelName, token);
    }

    /// <inheritdoc/>
    public async Task<List<string>> ToStringList(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        int retryAttempt = 0,
        int? originalRetryLimit = null,
        CancellationToken token = default)
    {
        Prompt prompt = FindPrompt(promptName);
        try
        {
            CompletionResult? completion = await Completion(prompt, inputs, modelProfile, modelName);

            if (completion is null)
                throw new Exception("InferService.ToStringList(): Null completion!");

            if (string.IsNullOrEmpty(completion.Selected))
                throw new Exception("InferService.ToStringList(): Null or empty selected string!");

            return completion.Selected.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .ToList();
        }
        catch (Exception e)
        {
            if (originalRetryLimit is null)
                originalRetryLimit = prompt.RetryAttempts;

            if (retryAttempt < (originalRetryLimit ?? 0))
            {
                Util.Log($"Retrying InferService.ToStringList() for prompt '{prompt.Name}'");

                string promptToRetry = promptName;
                if (prompt.RetryPrompt is not null)
                    promptToRetry = prompt.RetryPrompt;

                return await ToStringList(promptToRetry, inputs, modelProfile, modelName, retryAttempt + 1, originalRetryLimit, token);
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<List<string>> ToStringList(
        string promptName,
        Input? input,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        CancellationToken token = default)
    {
        List<Input>? inputs = input is not null ? [input] : null;
        return await ToStringList(promptName, inputs, modelProfile, modelName, token: token);
    }

    /// <inheritdoc/>
    public async Task<List<string>> ToStringListLimited(
        string promptName,
        List<Input>? inputs = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        int? maxLines = null,
        Func<string, bool>? evaluator = null,
        CancellationToken token = default)
    {
        List<string> lines = [];
        StringBuilder currentLine = new();
        StringBuilder allContent = new();
        int completedLineCount = 0;

        using CancellationTokenSource internalCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        try
        {
            Prompt prompt = FindPrompt(promptName);
            IAsyncEnumerable<string> streamResult = CompletionStream(
                prompt, inputs, modelProfile, modelName, null, internalCts.Token);

            await foreach (string chunk in streamResult)
            {
                allContent.Append(chunk);

                foreach (char c in chunk)
                {
                    if (c == '\n')
                    {
                        string completedLine = currentLine.ToString().Trim();
                        if (!string.IsNullOrEmpty(completedLine))
                        {
                            lines.Add(completedLine);
                            completedLineCount++;
                        }
                        currentLine.Clear();

                        if (maxLines.HasValue && completedLineCount >= maxLines.Value)
                        {
                            internalCts.Cancel();
                            return lines;
                        }

                        if (evaluator?.Invoke(allContent.ToString()) == true)
                        {
                            internalCts.Cancel();
                            return lines;
                        }
                    }
                    else if (c != '\r')
                    {
                        currentLine.Append(c);
                    }
                }
            }

            string finalLine = currentLine.ToString().Trim();
            if (!string.IsNullOrEmpty(finalLine))
                lines.Add(finalLine);
        }
        catch (OperationCanceledException) when (internalCts.Token.IsCancellationRequested && !token.IsCancellationRequested)
        {
            string finalLine = currentLine.ToString().Trim();
            if (!string.IsNullOrEmpty(finalLine))
                lines.Add(finalLine);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Util.Log($"ToStringListLimited exception: {e.Message}");
            throw;
        }

        return lines;
    }

    /// <inheritdoc/>
    public async Task<List<string>> ToStringListLimited(
        string promptName,
        Input? input = null,
        ModelProfile? modelProfile = null,
        string? modelName = null,
        int? maxLines = null,
        Func<string, bool>? evaluator = null,
        CancellationToken token = default)
    {
        List<Input>? inputs = input is not null ? [input] : null;
        return await ToStringListLimited(promptName, inputs, modelProfile, modelName, maxLines, evaluator, token);
    }

    #endregion


    // ======================
    //  Helpers
    // ======================

    #region Helpers

    /// <inheritdoc/>
    public Prompt FindPrompt(string name)
    {
        Prompt? found = prompts.Get(name);
        if (found is null)
            throw new Exception($"Could not find specified prompt: {name}");
        return found;
    }

    /// <summary>Resolves a provider by name; throws if not found.</summary>
    public ProviderProfile FindProvider(Prompt prompt, string provider)
    {
        ProviderProfile? found = providers.Get(provider);
        if (found is null)
            throw new Exception($"Could not find specified provider: {provider}");
        return found;
    }

    /// <inheritdoc/>
    public string? ListInputs(ModelProfile model, List<Input>? inputs)
    {
        if (inputs is null || !inputs.Any())
            return null;

        string? templateLine = model.InputItemMulti;
        if (inputs.Count == 1)
            templateLine = model.InputItem;

        string inputString = "";
        for (int num = 0; num < inputs.Count; ++num)
        {
            string line = templateLine;
            line = line.Replace("{iterator}", (num + 1).ToString());
            line = line.Replace("{label}", inputs[num].Label);
            line = line.Replace("{text}", inputs[num].Text);
            inputString += line;
        }

        return inputString;
    }

    #endregion


    // ======================
    //  Private Implementation
    // ======================

    #region Private Implementation

    private async Task<CompletionResult?> CallInference(
        object promptObject,
        Prompt prompt,
        ModelProfile model,
        Type? outputType = null,
        CancellationToken token = default)
    {
        Util.Log($"InferService.CallInference(prompt: '{prompt.Name}', model: '{model.Name}');");

        CompletionResult? response = null;
        try
        {
            int totalLength;

            if (model.Provider.InferenceClient is null)
                throw new Exception("InferenceClient is null!");

            GetGuidance(prompt, model, outputType, out GuidanceType? guidanceType, out string? guidanceString);
            int? inactivityTimeoutSeconds = GetEffectiveInactivityTimeoutSeconds(prompt, model);

            switch (promptObject)
            {
                case string promptString:
                {
                    totalLength = promptString.Length;
                    if (Util.EstTokenCountFromCharCount(totalLength) > model.TokenLimit)
                        throw new Exception("Too many tokens!");

                    response = await model.Provider.InferenceClient.GenerateAsync(
                        prompt: promptString,
                        model: model.ModelString,
                        temperature: (float?)SelectParam(model.Temperature, prompt.Temperature),
                        topK: (int?)SelectParam(model.TopK, prompt.TopK),
                        topP: (float?)SelectParam(model.TopP, prompt.TopP),
                        minP: (float?)SelectParam(model.MinP, prompt.MinP),
                        bestOf: (int?)SelectParam(model.BestOf, prompt.BestOf),
                        maxTokenType: model.MaxTokenType,
                        maxTokens: (int?)SelectParam(model.MaxTokens, prompt.MaxTokens),
                        frequencyPenalty: (float?)SelectParam(model.FrequencyPenalty, prompt.FrequencyPenalty),
                        presencePenalty: (float?)SelectParam(model.PresencePenalty, prompt.PresencePenalty),
                        repetitionPenalty: (float?)SelectParam(model.RepetitionPenalty, prompt.RepetitionPenalty),
                        stopSequences: ToArray(model.StopSequences),
                        guidanceType: guidanceType,
                        guidanceString: guidanceString,
                        useSearchGrounding: (bool?)SelectParam(model.UseSearchGrounding, prompt.UseSearchGrounding),
                        cancellationToken: token,
                        inactivityTimeoutSeconds: inactivityTimeoutSeconds);
                    break;
                }

                case List<Message> messages:
                {
                    totalLength = messages.Sum(msg => msg.Role.Length + msg.Content.Length);
                    if (Util.EstTokenCountFromCharCount(totalLength) > model.TokenLimit)
                        throw new Exception("Too many tokens!");

                    response = await model.Provider.InferenceClient.GenerateAsync(
                        messages: messages,
                        model: model.ModelString,
                        temperature: (float?)SelectParam(model.Temperature, prompt.Temperature),
                        topK: (int?)SelectParam(model.TopK, prompt.TopK),
                        topP: (float?)SelectParam(model.TopP, prompt.TopP),
                        minP: (float?)SelectParam(model.MinP, prompt.MinP),
                        bestOf: (int?)SelectParam(model.BestOf, prompt.BestOf),
                        maxTokenType: model.MaxTokenType,
                        maxTokens: (int?)SelectParam(model.MaxTokens, prompt.MaxTokens),
                        frequencyPenalty: (float?)SelectParam(model.FrequencyPenalty, prompt.FrequencyPenalty),
                        presencePenalty: (float?)SelectParam(model.PresencePenalty, prompt.PresencePenalty),
                        repetitionPenalty: (float?)SelectParam(model.RepetitionPenalty, prompt.RepetitionPenalty),
                        stopSequences: ToArray(model.StopSequences),
                        guidanceType: guidanceType,
                        guidanceString: guidanceString,
                        useSearchGrounding: (bool?)SelectParam(model.UseSearchGrounding, prompt.UseSearchGrounding),
                        cancellationToken: token,
                        inactivityTimeoutSeconds: inactivityTimeoutSeconds);
                    break;
                }
            }
        }
        catch (Exception e)
        {
            Util.Log($"InferService.CallInference Exception: \"{e.Message}\"");
        }

        string dump = $"CallInference:\n\nMessages:\n{prompt}\n\nOutput:\n";
        await Util.DumpLog(dump, "inference");

        return response;
    }

    private async IAsyncEnumerable<string> CallStreamingInference(
        object promptObject,
        Prompt prompt,
        ModelProfile model,
        Type? outputType = null,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        Util.Log($"InferService.CallStreamingInference(prompt: '{prompt.Name}', model: '{model.Name}');");

        int totalLength;
        StreamingResult<string> streamResult;

        if (model.Provider.InferenceClient is null)
            throw new Exception("InferenceClient is null!");

        GetGuidance(prompt, model, outputType, out GuidanceType? guidanceType, out string? guidanceString);
        int? inactivityTimeoutSeconds = GetEffectiveInactivityTimeoutSeconds(prompt, model);

        try
        {
            switch (promptObject)
            {
                case string promptString:
                {
                    totalLength = promptString.Length;
                    if (Util.EstTokenCountFromCharCount(totalLength) > model.TokenLimit)
                        throw new Exception("Too many tokens!");

                    streamResult = model.Provider.InferenceClient.GenerateStreamAsync(
                        prompt: promptString,
                        model: model.ModelString,
                        temperature: (float?)SelectParam(model.Temperature, prompt.Temperature),
                        topK: (int?)SelectParam(model.TopK, prompt.TopK),
                        topP: (float?)SelectParam(model.TopP, prompt.TopP),
                        minP: (float?)SelectParam(model.MinP, prompt.MinP),
                        bestOf: (int?)SelectParam(model.BestOf, prompt.BestOf),
                        maxTokenType: model.MaxTokenType,
                        maxTokens: (int?)SelectParam(model.MaxTokens, prompt.MaxTokens),
                        frequencyPenalty: (float?)SelectParam(model.FrequencyPenalty, prompt.FrequencyPenalty),
                        presencePenalty: (float?)SelectParam(model.PresencePenalty, prompt.PresencePenalty),
                        repetitionPenalty: (float?)SelectParam(model.RepetitionPenalty, prompt.RepetitionPenalty),
                        stopSequences: ToArray(model.StopSequences),
                        guidanceType: guidanceType,
                        guidanceString: guidanceString,
                        useSearchGrounding: (bool?)SelectParam(model.UseSearchGrounding, prompt.UseSearchGrounding),
                        cancellationToken: token,
                        inactivityTimeoutSeconds: inactivityTimeoutSeconds);
                    break;
                }

                case List<Message> messages:
                {
                    totalLength = messages.Sum(msg => msg.Role.Length + msg.Content.Length);
                    if (Util.EstTokenCountFromCharCount(totalLength) > model.TokenLimit)
                        throw new Exception("Too many tokens!");

                    streamResult = model.Provider.InferenceClient.GenerateStreamAsync(
                        messages: messages,
                        model: model.ModelString,
                        temperature: (float?)SelectParam(model.Temperature, prompt.Temperature),
                        topK: (int?)SelectParam(model.TopK, prompt.TopK),
                        topP: (float?)SelectParam(model.TopP, prompt.TopP),
                        minP: (float?)SelectParam(model.MinP, prompt.MinP),
                        bestOf: (int?)SelectParam(model.BestOf, prompt.BestOf),
                        maxTokenType: model.MaxTokenType,
                        maxTokens: (int?)SelectParam(model.MaxTokens, prompt.MaxTokens),
                        frequencyPenalty: (float?)SelectParam(model.FrequencyPenalty, prompt.FrequencyPenalty),
                        presencePenalty: (float?)SelectParam(model.PresencePenalty, prompt.PresencePenalty),
                        repetitionPenalty: (float?)SelectParam(model.RepetitionPenalty, prompt.RepetitionPenalty),
                        stopSequences: ToArray(model.StopSequences),
                        guidanceType: guidanceType,
                        guidanceString: guidanceString,
                        useSearchGrounding: (bool?)SelectParam(model.UseSearchGrounding, prompt.UseSearchGrounding),
                        cancellationToken: token,
                        inactivityTimeoutSeconds: inactivityTimeoutSeconds);
                    break;
                }

                default:
                    throw new Exception($"Unexpected prompt object type: {promptObject.GetType()}");
            }
        }
        catch (Exception e)
        {
            Util.Log($"InferService.CallStreamingInference Exception: \"{e.Message}\"");
            throw;
        }

        bool hasYieldedAnyChunks = false;

        await foreach (string chunk in streamResult.Stream.WithCancellation(token))
        {
            hasYieldedAnyChunks = true;
            yield return chunk;
        }

        StreamingMetadata metadata = await streamResult.Completion;
        if (!metadata.IsSuccess)
        {
            string errorMsg = $"Streaming failed: {metadata.ErrorMessage}";
            Util.Log(errorMsg);

            if (!hasYieldedAnyChunks)
                throw new Exception($"Streaming inference failed: {metadata.ErrorMessage}");
        }
    }

    private ModelProfile FindModel(Prompt prompt, ModelProfile? modelProfile, string? modelName)
    {
        if (modelProfile is not null)
        {
            if (modelProfile.Enabled)
                return modelProfile;
            throw new Exception($"Specified modelProfile but the model '{modelProfile.Name}' was not enabled.");
        }

        if (!string.IsNullOrEmpty(modelName))
        {
            ModelProfile? found = models.Get(modelName);
            if (found is not null && found.Enabled)
                return found;
            throw new Exception($"Specified modelString but an enabled model named '{modelName}' could not be found.");
        }

        if (prompt.PreferredModels != null && prompt.PreferredModels.Any())
        {
            foreach (string preferredModelName in prompt.PreferredModels)
            {
                ModelProfile? preferred = models.Get(preferredModelName);
                if (preferred is not null && preferred.Enabled)
                    return preferred;
            }
        }

        ModelProfile? tieredModel = models.Find(prompt.MinTier, prompt.IsCompletion(), prompt.BlockedModels);
        if (tieredModel is not null && tieredModel.Enabled)
            return tieredModel;

        ModelProfile? fallbackModel = models.Find(ModelTier.C, false, prompt.BlockedModels);
        if (fallbackModel is not null && fallbackModel.Enabled)
            return fallbackModel;

        throw new AggregateException($"Could not find model for prompt '{prompt.Name}'");
    }

    private async Task<bool> FilterCheck(Prompt prompt, List<Input>? inputs, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(prompt.Filter) || prompt.Filter.ToLower() == "false")
            return false;

        Prompt filterPrompt = FindPrompt(prompt.Filter);

        if (!string.IsNullOrEmpty(filterPrompt.Filter))
            throw new Exception("Filters can't have filters — recursive loops will occur!");

        CompletionResult? result = await Completion(filterPrompt, inputs, null, null, token: token);
        return result?.Selected != "foobar";
    }

    private static void GetGuidance(
        Prompt prompt,
        ModelProfile model,
        Type? outputType,
        out GuidanceType? guidanceType,
        out string? guidanceString)
    {
        guidanceType = null;
        guidanceString = null;

        if (outputType is null)
            return;

        if (!model.Provider.SupportsGuidance ?? false)
        {
            Util.Log($"GetGuidance: Provider {model.Provider.Name} does not support guidance");
            return;
        }

        try
        {
            switch (prompt.GuidanceSchema)
            {
                case GuidanceSchemaType.Disabled:
                    guidanceType = GuidanceType.Disabled;
                    break;

                case GuidanceSchemaType.Default:
                    guidanceType = model.Provider.DefaultGuidanceType;
                    guidanceString = model.Provider.DefaultGuidanceString;
                    break;

                case GuidanceSchemaType.JsonManual:
                    guidanceType = GuidanceType.Json;
                    guidanceString = prompt.Schema;
                    break;

                case GuidanceSchemaType.JsonAuto:
                    guidanceType = GuidanceType.Json;
                    guidanceString = Util.JsonStringFromType(outputType);
                    break;

                case GuidanceSchemaType.RegexManual:
                    guidanceType = GuidanceType.Regex;
                    guidanceString = prompt.Schema;
                    break;

                case GuidanceSchemaType.RegexAuto:
                    guidanceType = GuidanceType.Regex;
                    guidanceString = RegexGenerator.FromObject(outputType, prompt.ChainOfThought ?? false, "<|eot_id|>");
                    break;
            }
        }
        catch (Exception e)
        {
            Util.Log($"Guidance Exception: {e.Message}");
        }
    }

    private static int? GetEffectiveInactivityTimeoutSeconds(Prompt prompt, ModelProfile model)
    {
        int? modelSeconds = ParseTimeoutStringToSeconds(model.Timeout);
        int? promptSeconds = prompt.Timeout;
        int? effective = modelSeconds ?? promptSeconds;
        return ClampPositiveSeconds(effective);
    }

    private static int? ClampPositiveSeconds(int? secs)
    {
        if (secs is null) return null;
        return Math.Max(1, secs.Value);
    }

    private static int? ParseTimeoutStringToSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string s = value.Trim().ToLowerInvariant();
        if (int.TryParse(s, out int secs)) return secs;
        try
        {
            if (s.EndsWith("ms") && int.TryParse(s[..^2], out int ms)) return Math.Max(1, ms / 1000);
            if (s.EndsWith("s") && int.TryParse(s[..^1], out int sVal)) return sVal;
            if ((s.EndsWith("m") || s.EndsWith("min") || s.EndsWith("mins")) &&
                int.TryParse(s.TrimEnd('m', 'i', 'n', 's'), out int mVal)) return mVal * 60;
            if ((s.EndsWith("h") || s.EndsWith("hr") || s.EndsWith("hrs") || s.EndsWith("hour") || s.EndsWith("hours")) &&
                int.TryParse(new string(s.TakeWhile(char.IsDigit).ToArray()), out int hVal)) return hVal * 3600;
        }
        catch { }
        return null;
    }

    private static object? SelectParam(string? modelString, object? promptObj)
    {
        if (modelString is null) return promptObj;
        if (modelString is "disabled") return null;
        return promptObj switch
        {
            string => modelString,
            int => int.Parse(modelString),
            float => float.Parse(modelString),
            _ => throw new Exception($"Unexpected type: {promptObj.GetType()}")
        };
    }

    private static string[]? ToArray(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        return input.Split(' ');
    }

    private static bool ValidateObject<T>(object? inputObject, Prompt prompt)
    {
        if (prompt.RequireValidOutput is false) return true;
        if (inputObject is null) return false;
        return RecursivelyValidateObject(inputObject);
    }

    private static bool RecursivelyValidateObject(object obj)
    {
        try
        {
            foreach (System.Reflection.PropertyInfo property in obj.GetType().GetProperties())
            {
                object? value = null;
                try
                {
                    if (property.CanRead && property.GetIndexParameters().Length == 0)
                        value = property.GetValue(obj);
                }
                catch (Exception ex)
                {
                    Util.Log($"Warning: Could not read property '{property.Name}': {ex.Message}");
                    continue;
                }

                if (value is DBNull) value = null;

                try
                {
                    if (property.GetCustomAttributes(false).Any(attr => attr.GetType().Name == "RequiredAttribute"))
                    {
                        if (value == null || (value is string str && string.IsNullOrEmpty(str)))
                        {
                            Util.Log($"Validation failed: Required property '{property.Name}' is null or empty");
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Util.Log($"Warning: Could not check Required attribute for property '{property.Name}': {ex.Message}");
                }

                if (value is ICollection collection)
                {
                    try
                    {
                        foreach (object attr in property.GetCustomAttributes(false))
                        {
                            Type attrType = attr.GetType();
                            if (attrType.Name.Contains("MinItems") || attrType.Name.Contains("MinLength"))
                            {
                                int? minValue = GetAttributeIntValue(attr, ["Value", "MinItems", "MinLength", "Minimum"]);
                                if (minValue.HasValue && collection.Count < minValue.Value)
                                {
                                    Util.Log($"Validation failed: '{property.Name}' has {collection.Count} items, minimum: {minValue.Value}");
                                    return false;
                                }
                            }
                            if (attrType.Name.Contains("MaxItems") || attrType.Name.Contains("MaxLength"))
                            {
                                int? maxValue = GetAttributeIntValue(attr, ["Value", "MaxItems", "MaxLength", "Maximum"]);
                                if (maxValue.HasValue && collection.Count > maxValue.Value)
                                {
                                    Util.Log($"Validation failed: '{property.Name}' has {collection.Count} items, maximum: {maxValue.Value}");
                                    return false;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Util.Log($"Warning: Could not validate collection constraints for property '{property.Name}': {ex.Message}");
                    }
                }

                if (value != null && !Util.IsSimpleType(property.PropertyType))
                {
                    try
                    {
                        if (value is IEnumerable enumerable && value is not string)
                        {
                            foreach (object item in enumerable)
                            {
                                if (item != null && !RecursivelyValidateObject(item))
                                    return false;
                            }
                        }
                        else if (!RecursivelyValidateObject(value))
                        {
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Util.Log($"Warning: Could not recursively validate property '{property.Name}': {ex.Message}");
                    }
                }
            }

            foreach (System.Reflection.FieldInfo field in obj.GetType().GetFields())
            {
                object? value = null;
                try { value = field.GetValue(obj); }
                catch (Exception ex)
                {
                    Util.Log($"Warning: Could not read field '{field.Name}': {ex.Message}");
                    continue;
                }

                if (value is DBNull) value = null;

                try
                {
                    if (field.GetCustomAttributes(false).Any(attr => attr.GetType().Name == "RequiredAttribute"))
                    {
                        if (value == null || (value is string str && string.IsNullOrEmpty(str)))
                        {
                            Util.Log($"Validation failed: Required field '{field.Name}' is null or empty");
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Util.Log($"Warning: Could not check Required attribute for field '{field.Name}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Util.Log($"Warning: General validation error for object of type '{obj.GetType().Name}': {ex.Message}");
        }

        return true;
    }

    private static int? GetAttributeIntValue(object attribute, string[] possiblePropertyNames)
    {
        foreach (string propertyName in possiblePropertyNames)
        {
            try
            {
                System.Reflection.PropertyInfo? prop = attribute.GetType().GetProperty(propertyName);
                if (prop != null)
                {
                    object? value = prop.GetValue(attribute);
                    if (value != null && value != DBNull.Value)
                        return Convert.ToInt32(value);
                }
            }
            catch { }
        }
        return null;
    }

    private static bool TryParseEnum<TEnum>(string? text, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string? firstLine = text
            .Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(firstLine)) return false;

        string cleaned = firstLine.Trim('"', '\'', '`', ' ', '\t').TrimEnd('.', ';', ':');

        if (Enum.TryParse(cleaned, ignoreCase: true, out value)) return true;

        string all = text.Trim();
        string[] names = Enum.GetNames(typeof(TEnum));
        foreach (string name in names)
        {
            string pattern = $@"(?i)(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])";
            if (Regex.IsMatch(all, pattern) && Enum.TryParse(name, out value))
                return true;
        }

        return false;
    }

    #endregion
}

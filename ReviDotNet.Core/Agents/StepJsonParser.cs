// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Newtonsoft.Json;

namespace Revi;

/// <summary>
/// Tolerant parser for the per-step <see cref="AgentStepResponse"/> JSON. Providers with enforced
/// structured output (e.g. Gemini's responseSchema) return clean JSON, but providers without it
/// (e.g. Claude) sometimes wrap the object in ```json fences or surround it with a sentence. This
/// tries the raw text first, then reuses <see cref="Json.ExtractJson"/> (markdown-fence stripping,
/// bracket-region isolation, and lightweight repairs) before giving up.
/// </summary>
internal static class StepJsonParser
{
    public static AgentStepResponse? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 1. Fast path: the response is already clean JSON.
        var direct = TryDeserialize(raw);
        if (direct != null) return direct;

        // 2. Recover JSON wrapped in code fences / surrounding prose via the shared extractor.
        string extracted = Util.ExtractJson(raw);
        return string.IsNullOrEmpty(extracted) ? null : TryDeserialize(extracted);
    }

    private static AgentStepResponse? TryDeserialize(string json)
    {
        try { return JsonConvert.DeserializeObject<AgentStepResponse>(json); }
        catch { return null; }
    }
}

// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.JSInterop;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Helper for triggering browser downloads of artifact content (.pmt / .agent / .rcfg etc.)
/// Uses a small JS interop helper that constructs a Blob and clicks a synthetic anchor.
/// </summary>
public sealed class ExportService
{
    private readonly IJSRuntime _js;

    public ExportService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task DownloadTextAsync(string filename, string content, string mimeType = "text/plain")
    {
        await _js.InvokeVoidAsync("forgeExport.downloadText", filename, content, mimeType);
    }
}

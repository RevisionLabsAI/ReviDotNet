// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using ReviDotNet.Forge.Services.ApiKeys;

namespace ReviDotNet.Forge.Api;

public static class ApiKeyAuth
{
    public const string ClientIdKey = "ForgeClientId";
    public const string HeaderName = "X-Forge-ApiKey";

    public static async Task<bool> ValidateAsync(HttpContext context, IForgeApiKeyService keyService)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Missing X-Forge-ApiKey header" });
            return false;
        }

        var clientId = await keyService.ValidateAsync(raw!);
        if (clientId is null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or disabled API key" });
            return false;
        }

        context.Items[ClientIdKey] = clientId;
        return true;
    }
}

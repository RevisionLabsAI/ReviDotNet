// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Optional Forge gateway integration. When a forge.rcfg is present and enabled,
/// Infer.Completion and Infer.CompletionStream route through Forge instead of calling
/// providers directly. When absent or disabled, behavior is identical to before.
/// </summary>
public static class ForgeManager
{
    /// <summary>Whether Forge has been configured and is active.</summary>
    public static bool IsConfigured { get; private set; }

    /// <summary>The active Forge inference client used for gateway routing.</summary>
    public static ForgeInferClient? Client { get; private set; }

    /// <summary>The active Forge reporter used for direct-route usage reporting.</summary>
    public static ForgeReporter? Reporter { get; private set; }

    /// <summary>The configuration used to initialise the Forge connection.</summary>
    public static ForgeInferConfig? Config { get; private set; }

    /// <summary>
    /// Initialises Forge with the supplied configuration.
    /// </summary>
    /// <param name="config">The Forge connection configuration.</param>
    public static void Init(ForgeInferConfig config)
    {
        Client?.Dispose();
        Reporter?.Dispose();
        Client = new ForgeInferClient(config);
        Reporter = new ForgeReporter(config.ForgeUrl, config.ApiKey);
        Config = config;
        IsConfigured = true;
        Util.Log($"ForgeManager: configured for {config.ForgeUrl} as client '{config.ClientId}'");
    }

    /// <summary>
    /// Resets Forge configuration and disposes active clients.
    /// </summary>
    public static void Reset()
    {
        Client?.Dispose();
        Reporter?.Dispose();
        Client = null;
        Reporter = null;
        Config = null;
        IsConfigured = false;
    }

    public static void Load()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RConfigs", "forge.rcfg");
            if (!File.Exists(path)) return;

            var data = RConfigParser.Read(path);

            if (!data.TryGetValue("general.enabled", out var enabledStr)
                || !bool.TryParse(enabledStr, out var enabled)
                || !enabled)
                return;

            data.TryGetValue("general.forge-url", out var forgeUrl);
            data.TryGetValue("general.api-key", out var apiKey);
            data.TryGetValue("general.client-id", out var clientId);
            data.TryGetValue("general.timeout-seconds", out var timeoutStr);

            if (string.IsNullOrWhiteSpace(forgeUrl)) return;

            // Resolve "environment" sentinel for the API key
            if (string.Equals(apiKey, "environment", StringComparison.OrdinalIgnoreCase))
                apiKey = Environment.GetEnvironmentVariable("FORGE_API_KEY") ?? string.Empty;

            Init(new ForgeInferConfig
            {
                ForgeUrl = forgeUrl,
                ApiKey = apiKey ?? string.Empty,
                ClientId = clientId ?? "unknown",
                TimeoutSeconds = int.TryParse(timeoutStr, out var t) ? t : 300
            });
        }
        catch (Exception ex)
        {
            Util.Log($"ForgeManager: failed to load forge.rcfg: {ex.Message}");
        }
    }
}

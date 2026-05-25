// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Revi;

namespace ReviDotNet.Forge.Services;

/// <summary>
/// Persistence helper for <see cref="ProviderProfile"/> entries — writes new .rcfg files to
/// RConfigs/Providers/ and triggers a registry reload. Existing providers are read-only
/// from the UI (per design); this service exists to support the "Add New" flow only.
/// </summary>
public sealed class ProviderRegistryService
{
    private readonly string _sourcePath;
    private readonly IProviderManager _providers;

    public ProviderRegistryService(IConfiguration config, IProviderManager providers)
    {
        _sourcePath = config["Forge:ProvidersSourcePath"] ?? "RConfigs/Providers";
        _providers = providers;
    }

    public string Serialize(ProviderProfile profile) => RConfigSerializer.Serialize(profile);

    public async Task SaveNewAsync(ProviderProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Provider name is required.", nameof(profile));

        string path = Path.Combine(_sourcePath, profile.Name + ".rcfg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Serialize(profile), Encoding.UTF8, ct);

        await _providers.LoadAsync(Assembly.GetExecutingAssembly(), ct);
    }
}

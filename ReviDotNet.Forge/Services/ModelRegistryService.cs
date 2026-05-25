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
/// Persistence helper for <see cref="ModelProfile"/> entries — writes new .rcfg files to
/// RConfigs/Models/ and triggers a registry reload. Existing models are read-only from
/// the UI (per design); this service exists to support the "Add New" flow only.
/// </summary>
public sealed class ModelRegistryService
{
    private readonly string _sourcePath;
    private readonly IModelManager _models;

    public ModelRegistryService(IConfiguration config, IModelManager models)
    {
        _sourcePath = config["Forge:ModelsSourcePath"] ?? "RConfigs/Models";
        _models = models;
    }

    public string Serialize(ModelProfile profile) => RConfigSerializer.Serialize(profile);

    public async Task SaveNewAsync(ModelProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Model name is required.", nameof(profile));

        string path = Path.Combine(_sourcePath, profile.Name + ".rcfg");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Serialize(profile), Encoding.UTF8, ct);

        // Reload manager so the new model appears in /models and dropdowns.
        await _models.LoadAsync(Assembly.GetExecutingAssembly(), ct);
    }
}

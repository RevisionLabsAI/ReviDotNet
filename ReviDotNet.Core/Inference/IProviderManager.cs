// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using System.Reflection;

namespace Revi;

/// <summary>DI interface for the provider registry.</summary>
public interface IProviderManager
{
    /// <summary>Loads provider profiles from the application assembly.</summary>
    Task LoadAsync(Assembly assembly, CancellationToken cancellationToken = default);

    /// <summary>Returns the provider profile with the given name, or null if not found.</summary>
    ProviderProfile? Get(string name);

    /// <summary>Returns all loaded provider profiles.</summary>
    List<ProviderProfile> GetAll();

    /// <summary>Programmatically adds a provider profile to the registry.</summary>
    void Add(ProviderProfile provider);
}

// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.DependencyInjection;

namespace Revi;

/// <summary>
/// Minimal, optional service locator to bridge legacy static logging (Util.Log)
/// to the DI-based ReviLogger without refactoring all call sites at once.
/// </summary>
public static class ReviServiceLocator
{
    private static IServiceProvider? _provider;

    /// <summary>
    /// Assign the application's root service provider at startup.
    /// </summary>
    public static void SetProvider(IServiceProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Try to retrieve IReviLogger from the assigned provider.
    /// </summary>
    public static bool TryGetLogger(out IReviLogger? logger)
    {
        logger = null;
        try
        {
            IServiceProvider? p = _provider;
            if (p == null)
                return false;

            logger = p.GetService<IReviLogger>();
            return logger != null;
        }
        catch
        {
            logger = null;
            return false;
        }
    }

    /// <summary>
    /// Try to retrieve a typed IReviLogger<T> from the assigned provider.
    /// </summary>
    public static bool TryGetLogger<T>(out IReviLogger<T>? logger)
    {
        logger = default;
        try
        {
            IServiceProvider? p = _provider;
            if (p == null)
                return false;

            logger = p.GetService<IReviLogger<T>>();
            return logger != null;
        }
        catch
        {
            logger = default;
            return false;
        }
    }

    /// <summary>
    /// Try to retrieve any registered service of type <typeparamref name="T"/> from the assigned provider.
    /// Returns false when no provider is set or the service is not registered.
    /// </summary>
    public static bool TryGetService<T>(out T? service) where T : class
    {
        service = default;
        try
        {
            IServiceProvider? p = _provider;
            if (p == null)
                return false;

            service = p.GetService<T>();
            return service != null;
        }
        catch
        {
            service = default;
            return false;
        }
    }
}

// =================================================================================
//   Copyright © 2025 Revision Labs, Inc. - All Rights Reserved
// =================================================================================
//   This is proprietary and confidential source code of Revision Labs, Inc., and
//   is safeguarded by international copyright laws. Unauthorized use, copying, 
//   modification, or distribution is strictly forbidden.
//
//   If you are not authorized and have this file, notify Revision Labs at 
//   contact@rlab.ai and delete it immediately.
//
//   See LICENSE.txt in the project root for full license information.
// =================================================================================

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
}

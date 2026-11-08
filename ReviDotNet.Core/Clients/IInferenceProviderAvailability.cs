// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Tells automatic model selection whether a provider may be called right now. Supplied by the host:
/// ReviDotNet reports every classified failure through <see cref="InferenceProviderMonitor"/> and stops
/// there, because deciding that a provider is out of service — and for how long — is a product
/// decision. A host that tracks provider health registers this so a prompt with no explicit model
/// moves to the next preferred or same-tier model instead of spending its call on a provider that
/// cannot answer (an exhausted account, a revoked key, a cooldown).
/// </summary>
/// <remarks>
/// Optional. With none registered, selection is exactly what it was: the first enabled preferred
/// model, then the tier fallback. A model named explicitly by the caller is never substituted.
/// <para>
/// <see cref="IsUsable"/> is asked about candidates one at a time, in selection order, and the first
/// provider it approves is used. An implementation may therefore hand out a single recovery trial
/// from it (BetterNamer's tracker does): the caller that is told "usable" does go on to make the call.
/// </para>
/// </remarks>
public interface IInferenceProviderAvailability
{
    /// <summary>Whether the named provider may be called now.</summary>
    /// <param name="providerName">The provider profile name, as reported to <see cref="InferenceProviderMonitor"/>.</param>
    /// <returns><see langword="true"/> when a call may be made.</returns>
    bool IsUsable(string providerName);
}

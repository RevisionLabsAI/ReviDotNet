// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Typed variant of IReviLogger that mirrors Microsoft.Extensions.Logging ILogger{T}.
/// Provides category type information via generic parameter while preserving
/// all functionality of the non-generic IReviLogger.
/// </summary>
/// <typeparam name="T">Category type for the logger</typeparam>
public interface IReviLogger<T> : IReviLogger
{
}

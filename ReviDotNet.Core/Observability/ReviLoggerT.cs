// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using Microsoft.Extensions.Configuration;

namespace Revi;

/// <summary>
/// Generic typed implementation mirroring the non-generic ReviLogger.
/// This enables dependency injection of IReviLogger{T} while preserving
/// all existing non-generic behavior.
/// </summary>
/// <typeparam name="T">Category type</typeparam>
public class ReviLogger<T> : ReviLogger, IReviLogger<T>
{
	// Category/type name exposed to base for prefix enrichment when enabled
	protected override string? CategoryName => typeof(T).Name;

	public ReviLogger(IRlogEventPublisher eventPublisher, IConfiguration configuration)
		: base(eventPublisher, configuration)
	{
	}
}

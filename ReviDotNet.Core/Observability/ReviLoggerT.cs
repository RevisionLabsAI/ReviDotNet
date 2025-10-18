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

// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Interface for publishing log events to external consumers
/// </summary>
public interface IRlogEventPublisher
{
    /// <summary>
    /// Publishes a log event asynchronously
    /// </summary>
    /// <param name="rlogEvent">The log event to publish</param>
    /// <returns>A task representing the async operation</returns>
    Task PublishLogEventAsync(RlogEvent rlogEvent);
    
    /// <summary>
    /// Publishes a log event synchronously (fire-and-forget)
    /// </summary>
    /// <param name="rlogEvent">The log event to publish</param>
    void PublishLogEvent(RlogEvent rlogEvent);
}
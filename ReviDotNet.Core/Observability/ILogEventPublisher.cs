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

namespace Revi;

/// <summary>
/// Interface for publishing log events to external consumers
/// </summary>
public interface ILogEventPublisher
{
    /// <summary>
    /// Publishes a log event asynchronously
    /// </summary>
    /// <param name="logEvent">The log event to publish</param>
    /// <returns>A task representing the async operation</returns>
    Task PublishLogEventAsync(LogEvent logEvent);
    
    /// <summary>
    /// Publishes a log event synchronously (fire-and-forget)
    /// </summary>
    /// <param name="logEvent">The log event to publish</param>
    void PublishLogEvent(LogEvent logEvent);
}
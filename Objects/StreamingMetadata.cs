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
/// Contains metadata about a completed streaming operation.
/// </summary>
public class StreamingMetadata
{
    /// <summary>
    /// Indicates whether the streaming operation completed successfully.
    /// </summary>
    public bool IsSuccess { get; set; }
    
    /// <summary>
    /// Error message if the operation failed, null if successful.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// The number of chunks/items that were successfully yielded.
    /// </summary>
    public int ChunkCount { get; set; }
    
    /// <summary>
    /// The total duration of the streaming operation.
    /// </summary>
    public TimeSpan Duration { get; set; }
    
    /// <summary>
    /// The exception that caused the failure, if any.
    /// </summary>
    public Exception? Exception { get; set; }
    
    /// <summary>
    /// The time when the streaming operation started.
    /// </summary>
    public DateTime StartTime { get; set; }
    
    /// <summary>
    /// The time when the streaming operation completed.
    /// </summary>
    public DateTime EndTime { get; set; }
    
    /// <summary>
    /// Additional context or debug information about the operation.
    /// </summary>
    public string? Context { get; set; }
}
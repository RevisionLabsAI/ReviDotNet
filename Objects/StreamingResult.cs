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
/// Represents the result of a streaming operation, including both the stream and completion metadata.
/// </summary>
/// <typeparam name="T">The type of items yielded by the stream.</typeparam>
public class StreamingResult<T>
{
    /// <summary>
    /// The async enumerable stream that yields items as they are generated.
    /// </summary>
    public IAsyncEnumerable<T> Stream { get; set; } = null!;
    
    /// <summary>
    /// A task that completes when the streaming operation finishes, providing metadata about the operation.
    /// </summary>
    public Task<StreamingMetadata> Completion { get; set; } = null!;
}
// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

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
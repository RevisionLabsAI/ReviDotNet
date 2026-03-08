// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

namespace Revi;

/// <summary>
/// Helper class to track streaming metadata.
/// </summary>
public class StreamingMetadataTracker
{
    private readonly DateTime _startTime;
    private readonly TaskCompletionSource<StreamingMetadata> _completionSource;
    private int _chunkCount;

    public StreamingMetadataTracker(DateTime startTime)
    {
        _startTime = startTime;
        _completionSource = new TaskCompletionSource<StreamingMetadata>();
    }

    public Task<StreamingMetadata> CompletionTask => _completionSource.Task;

    public void IncrementChunkCount()
    {
        Interlocked.Increment(ref _chunkCount);
    }

    public void CompleteSuccessfully()
    {
        var endTime = DateTime.UtcNow;
        var metadata = new StreamingMetadata
        {
            IsSuccess = true,
            ErrorMessage = null,
            Exception = null,
            ChunkCount = _chunkCount,
            Duration = endTime - _startTime,
            StartTime = _startTime,
            EndTime = endTime,
            Context = "Streaming completed successfully"
        };
        _completionSource.SetResult(metadata);
    }

    public void CompleteWithError(Exception exception)
    {
        var endTime = DateTime.UtcNow;
        var metadata = new StreamingMetadata
        {
            IsSuccess = false,
            ErrorMessage = exception.Message,
            Exception = exception,
            ChunkCount = _chunkCount,
            Duration = endTime - _startTime,
            StartTime = _startTime,
            EndTime = endTime,
            Context = "Streaming failed with error"
        };
        _completionSource.SetResult(metadata);
    }

    public void CompleteCanceled(OperationCanceledException exception)
    {
        var endTime = DateTime.UtcNow;
        var metadata = new StreamingMetadata
        {
            IsSuccess = false,
            ErrorMessage = "Operation was canceled",
            Exception = exception,
            ChunkCount = _chunkCount,
            Duration = endTime - _startTime,
            StartTime = _startTime,
            EndTime = endTime,
            Context = "Streaming was canceled"
        };
        _completionSource.SetResult(metadata);
    }
}

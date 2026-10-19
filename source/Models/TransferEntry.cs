namespace StreamZilla.Models;

public class TransferEntry
{
    private readonly object _chunkLock = new object();
    private long _completedLength;

    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public long TotalLength { get; set; }
    public string Status { get; set; } = "Queued";
    public string ErrorMessage { get; set; } = "";
    public TransferDirection Direction { get; set; } = TransferDirection.Download;

    public long[] ChunkBytes { get; set; } = Array.Empty<long>();

    public long CompletedLength
    {
        get
        {
            lock (_chunkLock)
            {
                return _completedLength;
            }
        }
        set
        {
            lock (_chunkLock)
            {
                _completedLength = Math.Max(0, Math.Min(value, TotalLength));
            }
        }
    }

    public void SetChunkBytes(int chunkIndex, long bytes)
    {
        lock (_chunkLock)
        {
            if (chunkIndex < 0 || chunkIndex >= ChunkBytes.Length)
            {
                return;
            }

            ChunkBytes[chunkIndex] = Math.Max(bytes, ChunkBytes[chunkIndex]);

            long total = 0;
            foreach (long chunkTotal in ChunkBytes)
            {
                total += chunkTotal;
            }

            if (TotalLength > 0)
            {
                total = Math.Min(total, TotalLength);
            }

            _completedLength = total;
        }
    }

    public void ResetChunkBytes(int chunkCount)
    {
        lock (_chunkLock)
        {
            ChunkBytes = new long[chunkCount];
            _completedLength = 0;
        }
    }

    public bool IsChunkComplete(int chunkIndex, long chunkSize)
    {
        lock (_chunkLock)
        {
            if (chunkIndex < 0 || chunkIndex >= ChunkBytes.Length)
            {
                return false;
            }

            return ChunkBytes[chunkIndex] >= chunkSize;
        }
    }

    public bool IsFinished
    {
        get
        {
            return Status.Equals("Complete", StringComparison.OrdinalIgnoreCase) ||
                   Status.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
                   Status.Equals("Removed", StringComparison.OrdinalIgnoreCase);
        }
    }
}

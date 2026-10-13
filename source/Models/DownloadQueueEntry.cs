namespace StreamZilla.Models;

public class DownloadQueueEntry
{
    public string Id { get; set; } = "";
    public string FileName { get; set; } = "";
    public string RemotePath { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public long TotalLength { get; set; }
    public long CompletedLength { get; set; }
    public long[]? ChunkBytes { get; set; }
    public TransferDirection Direction { get; set; } = TransferDirection.Download;
}

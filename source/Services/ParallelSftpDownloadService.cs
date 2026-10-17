using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using StreamZilla.Models;

namespace StreamZilla.Services;

public sealed class ParallelSftpDownloadService
{
    private const int BufferSize = 1024 * 1024;
    public const string SidecarSuffix = ".streamzilla-progress";
    private const int ConnectAttempts = 3;
    private const int ProgressIntervalMilliseconds = 150;

    public event Action<long>? OnProgress;

    public async Task DownloadAsync(
        AppSettings settings,
        Entry remoteFile,
        string localPath,
        int connections,
        DownloadEntry entry,
        CancellationToken token)
    {
        long totalSize = remoteFile.Size;

        if (totalSize <= 0)
        {
            throw new InvalidOperationException("The remote file size is unknown or zero.");
        }

        entry.TotalLength = totalSize;

        string destination = Path.GetDirectoryName(localPath) ?? ".";
        Directory.CreateDirectory(destination);

        string sidecarPath = localPath + SidecarSuffix;
        bool isNewDownload = !File.Exists(sidecarPath);

        int expectedChunkCount = Math.Max(
            1,
            Math.Min(connections, (int)Math.Max(1, totalSize / (1024 * 1024))));

        if (isNewDownload)
        {
            entry.ResetChunkBytes(expectedChunkCount);

            using FileStream createStream = new(
                localPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read);

            WriteDownloadSidecar(sidecarPath, entry, totalSize);
            LogService.Info($"Starting download to {localPath} ({totalSize} bytes, {expectedChunkCount} stream(s)).");
        }
        else
        {
            if (entry.ChunkBytes.Length != expectedChunkCount)
            {
                entry.ResetChunkBytes(expectedChunkCount);
                LogService.Info($"Restarting download for {remoteFile.FullPath}: saved chunk state was incompatible.");
            }

            entry.CompletedLength = entry.ChunkBytes.Sum();
            entry.Status = "Resuming";
            WriteDownloadSidecar(sidecarPath, entry, totalSize);

            LogService.Info($"Resuming download: {remoteFile.FullPath} at {entry.CompletedLength} of {totalSize} bytes.");
        }

        List<Task> tasks = new();
        object sidecarLock = new();

        for (int i = 0; i < expectedChunkCount; i++)
        {
            long chunkLength = ChunkLength(i, expectedChunkCount, totalSize);

            if (entry.IsChunkComplete(i, chunkLength))
            {
                continue;
            }

            long start = i * (totalSize / expectedChunkCount);
            long end = i == expectedChunkCount - 1 ? totalSize - 1 : start + chunkLength - 1;

            int chunkIndex = i;
            tasks.Add(DownloadChunkAsync(
                settings,
                remoteFile.FullPath,
                localPath,
                sidecarPath,
                start,
                end,
                entry,
                chunkIndex,
                sidecarLock,
                token));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            lock (sidecarLock)
            {
                WriteDownloadSidecar(sidecarPath, entry, totalSize);
            }

            entry.Status = token.IsCancellationRequested ? "Paused" : "Error";
            LogService.Error($"Download failed for {remoteFile.FullPath}: {ex.Message}");
            throw;
        }

        if (!token.IsCancellationRequested)
        {
            for (int i = 0; i < expectedChunkCount; i++)
            {
                entry.SetChunkBytes(i, ChunkLength(i, expectedChunkCount, totalSize));
            }

            entry.CompletedLength = totalSize;
            WriteDownloadSidecar(sidecarPath, entry, totalSize);

            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }

            entry.Status = "Complete";
            LogService.Info($"Download complete: {remoteFile.FullPath} -> {localPath}");
        }
    }

    private async Task DownloadChunkAsync(
        AppSettings settings,
        string remotePath,
        string localPath,
        string sidecarPath,
        long start,
        long end,
        DownloadEntry entry,
        int chunkIndex,
        object sidecarLock,
        CancellationToken token)
    {
        SftpClient client = await ConnectWithRetryAsync(settings, token);

        try
        {
            using Stream remote = client.OpenRead(remotePath);
            remote.Seek(start, SeekOrigin.Begin);

            using FileStream local = new(
                localPath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite);

            long position = start;
            long chunkWritten = entry.ChunkBytes[chunkIndex];
            long lastNotified = Environment.TickCount64;

            if (chunkWritten > 0)
            {
                position += chunkWritten;
                remote.Seek(position, SeekOrigin.Begin);
                local.Seek(position, SeekOrigin.Begin);
            }

            byte[] buffer = new byte[BufferSize];

            while (position <= end)
            {
                token.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(buffer.Length, end - position + 1);
                int read = remote.Read(buffer, 0, toRead);

                if (read <= 0)
                {
                    break;
                }

                local.Write(buffer, 0, read);
                position += read;
                chunkWritten += read;
                entry.SetChunkBytes(chunkIndex, chunkWritten);

                long now = Environment.TickCount64;

                if (now - lastNotified >= ProgressIntervalMilliseconds || position > end)
                {
                    lastNotified = now;
                    OnProgress?.Invoke(entry.CompletedLength);

                    lock (sidecarLock)
                    {
                        WriteDownloadSidecar(sidecarPath, entry, entry.TotalLength);
                    }
                }
            }
        }
        finally
        {
            try
            {
                client.Disconnect();
            }
            catch
            {
            }

            client.Dispose();
        }
    }

    private static void WriteDownloadSidecar(
        string sidecarPath,
        DownloadEntry entry,
        long totalLength)
    {
        try
        {
            TransferSidecar sidecar = new()
            {
                TransferId = entry.Id,
                Direction = "Download",
                RemotePath = entry.RemotePath,
                LocalPath = entry.LocalPath,
                TotalLength = totalLength,
                CompletedLength = entry.CompletedLength,
                ChunkBytes = entry.ChunkBytes,
                UpdatedAtUtc = DateTime.UtcNow
            };

            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(sidecar));
        }
        catch (Exception ex)
        {
            LogService.Warn($"Could not write download sidecar {sidecarPath}: {ex.Message}");
        }
    }

    private static async Task<SftpClient> ConnectWithRetryAsync(
        AppSettings settings,
        CancellationToken token)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= ConnectAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            SftpClient client = CreateClient(settings);

            try
            {
                await Task.Run(client.Connect, token);
                return client;
            }
            catch (Exception ex) when (attempt < ConnectAttempts && !token.IsCancellationRequested)
            {
                lastError = ex;
                LogService.Warn($"SFTP connection attempt {attempt} failed: {ex.Message}. Retrying.");
                client.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), token);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Could not connect to {settings.Host}:{settings.Port} after {ConnectAttempts} attempts.",
            lastError);
    }

    public static SftpClient CreateClient(AppSettings settings)
    {
        PasswordAuthenticationMethod auth = new(settings.Username, settings.Password);

        ConnectionInfo info = new(
            settings.Host,
            settings.Port,
            settings.Username,
            auth)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        return new SftpClient(info)
        {
            OperationTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        };
    }

    private static long ChunkLength(int chunkIndex, int chunkCount, long totalSize)
    {
        long chunkSize = totalSize / chunkCount;

        return chunkIndex == chunkCount - 1
            ? totalSize - (chunkSize * (chunkCount - 1))
            : chunkSize;
    }

    public static string GetSidecarPath(string localPath) =>
        localPath + SidecarSuffix;

    public static string GetTempFilePath(string localPath) =>
        GetSidecarPath(localPath);

    private sealed class TransferSidecar
    {
        public string TransferId { get; set; } = "";
        public string Direction { get; set; } = "";
        public string RemotePath { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public long TotalLength { get; set; }
        public long CompletedLength { get; set; }
        public long[] ChunkBytes { get; set; } = Array.Empty<long>();
        public DateTime UpdatedAtUtc { get; set; }
    }
}

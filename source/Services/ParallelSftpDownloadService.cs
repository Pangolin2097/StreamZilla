using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using StreamZilla.Models;

namespace StreamZilla.Services;

public sealed class ParallelSftpDownloadService
{
    private const int BufferSize = 1024 * 1024;
    private const string TempFileSuffix = ".streamzilla-download";
    private const int ConnectAttempts = 3;
    private const int ProgressIntervalMilliseconds = 150;

    public event Action<long>? OnProgress;

    public async Task DownloadAsync(
        AppSettings settings,
        RemoteEntry remoteFile,
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

        string tempPath = localPath + TempFileSuffix;

        bool tempExists = File.Exists(tempPath);
        bool sizeMatches = tempExists && new FileInfo(tempPath).Length == totalSize;

        int expectedChunkCount = Math.Max(1, Math.Min(connections, (int)Math.Max(1, totalSize / (1024 * 1024))));

        if (!tempExists || !sizeMatches)
        {
            using (FileStream createStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                createStream.SetLength(totalSize);
            }

            entry.ResetChunkBytes(expectedChunkCount);

            LogService.Info($"Starting download to temporary file: {tempPath} ({totalSize} bytes, {expectedChunkCount} stream(s)).");
        }
        else
        {
            if (entry.ChunkBytes.Length != expectedChunkCount)
            {
                entry.ResetChunkBytes(expectedChunkCount);
                LogService.Info($"Restarting download for {remoteFile.FullPath}: saved chunk state was missing or incompatible.");
            }
            else
            {
                int remainingChunks = 0;

                for (int i = 0; i < expectedChunkCount; i++)
                {
                    long chunkLength = ChunkLength(i, expectedChunkCount, totalSize);

                    if (!entry.IsChunkComplete(i, chunkLength))
                    {
                        remainingChunks++;
                    }
                }

                entry.CompletedLength = entry.ChunkBytes.Sum();
                entry.Status = "Resuming";

                LogService.Info($"Resuming download: {remoteFile.FullPath} at {entry.CompletedLength} of {totalSize} bytes; {remainingChunks} chunk(s) remaining.");
            }
        }

        List<Task> tasks = new List<Task>();

        for (int i = 0; i < expectedChunkCount; i++)
        {
            long chunkLength = ChunkLength(i, expectedChunkCount, totalSize);

            if (entry.IsChunkComplete(i, chunkLength))
            {
                continue;
            }

            long start = i * (totalSize / expectedChunkCount);
            long end = (i == expectedChunkCount - 1) ? totalSize - 1 : start + chunkLength - 1;

            int chunkIndex = i;
            tasks.Add(DownloadChunkAsync(settings, remoteFile.FullPath, tempPath, start, end, entry, chunkIndex, token));
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
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

            await FinalizeDownloadAsync(tempPath, localPath);

            entry.Status = "Complete";
            LogService.Info($"Download complete: {remoteFile.FullPath} -> {localPath}");
        }
    }

    private static async Task FinalizeDownloadAsync(string tempPath, string localPath)
    {
        const int maxAttempts = 10;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Move(tempPath, localPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                await Task.Delay(250);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                await Task.Delay(250);
            }
        }
    }

    public static string GetTempFilePath(string localPath)
    {
        return localPath + TempFileSuffix;
    }

    private static long ChunkLength(int chunkIndex, int chunkCount, long totalSize)
    {
        long chunkSize = totalSize / chunkCount;

        if (chunkIndex == chunkCount - 1)
        {
            return totalSize - (chunkSize * (chunkCount - 1));
        }

        return chunkSize;
    }

    private Task DownloadChunkAsync(
        AppSettings settings,
        string remotePath,
        string tempPath,
        long start,
        long end,
        DownloadEntry entry,
        int chunkIndex,
        CancellationToken token)
    {
        return Task.Run(async () =>
        {
            SftpClient client = await ConnectWithRetryAsync(settings, token);

            try
            {
                using (Stream remote = client.OpenRead(remotePath))
                {
                    remote.Seek(start, SeekOrigin.Begin);

                    using (FileStream local = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                    {
                        local.Seek(start, SeekOrigin.Begin);

                        byte[] buffer = new byte[BufferSize];
                        long position = start;
                        long chunkWritten = entry.GetChunkBytes(chunkIndex);
                        long lastNotified = Environment.TickCount64;

                        if (chunkWritten > 0)
                        {
                            position += chunkWritten;
                            remote.Seek(position, SeekOrigin.Begin);
                            local.Seek(position, SeekOrigin.Begin);
                        }

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
                            }
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
        }, token);
    }

    private static async Task<SftpClient> ConnectWithRetryAsync(AppSettings settings, CancellationToken token)
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
            $"Could not connect to {settings.Host}:{settings.Port} after {ConnectAttempts} attempts.", lastError);
    }

    public static SftpClient CreateClient(AppSettings settings)
    {
        AuthenticationMethod auth = new PasswordAuthenticationMethod(settings.Username, settings.Password);

        ConnectionInfo info = new ConnectionInfo(settings.Host, settings.Port, settings.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        SftpClient client = new SftpClient(info)
        {
            OperationTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        };

        return client;
    }
}

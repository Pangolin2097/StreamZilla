using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using StreamZilla.Models;

namespace StreamZilla.Services
{
    public sealed class ParallelSftpTransferService : IParallelTransferService
    {
        private const int ChunkSize = 8 * 1024 * 1024; // 8 MiB

        private sealed class TransferState
        {
            public string TransferId { get; set; } = string.Empty;
            public long TotalLength { get; set; }
            public int ChunkSize { get; set; }
            public List<int> CompletedChunks { get; set; } = new();
        }

        public async Task DownloadAsync(
            AppSettings settings,
            RemoteEntry remoteFile,
            string localPath,
            int streams,
            TransferEntry entry,
            CancellationToken cancellationToken)
        {
            if (remoteFile == null)
            {
                throw new ArgumentNullException(nameof(remoteFile));
            }

            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new ArgumentException("Local path is required.", nameof(localPath));
            }

            streams = Math.Clamp(streams, 1, 8);

            string sidecarPath = GetSidecarPath(localPath);
            long totalLength = remoteFile.Size;
            int chunkCount = GetChunkCount(totalLength);

            TransferState state = LoadOrCreateState(
                sidecarPath,
                entry.Id,
                totalLength,
                chunkCount);

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            await using (FileStream localStream = new(
                localPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read))
            {
                if (localStream.Length < totalLength)
                {
                    localStream.SetLength(totalLength);
                }
            }

            await TransferChunksAsync(
                settings,
                state,
                chunkCount,
                streams,
                cancellationToken,
                async (chunkIndex, chunkStart, chunkLength, token) =>
                {
                    await DownloadChunkAsync(
                        settings,
                        remoteFile.FullPath,
                        localPath,
                        chunkStart,
                        chunkLength,
                        token);
                });

            File.Delete(sidecarPath);
        }

        public async Task UploadAsync(
            AppSettings settings,
            string localPath,
            string remotePath,
            int streams,
            TransferEntry entry,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(localPath))
            {
                throw new ArgumentException("Local path is required.", nameof(localPath));
            }

            if (string.IsNullOrWhiteSpace(remotePath))
            {
                throw new ArgumentException("Remote path is required.", nameof(remotePath));
            }

            streams = Math.Clamp(streams, 1, 8);

            FileInfo localFile = new FileInfo(localPath);

            if (!localFile.Exists)
            {
                throw new FileNotFoundException("Local file was not found.", localPath);
            }

            long totalLength = localFile.Length;
            int chunkCount = GetChunkCount(totalLength);
            string sidecarPath = GetSidecarPath(remotePath);

            TransferState state = LoadOrCreateState(
                sidecarPath,
                entry.Id,
                totalLength,
                chunkCount);

            CreateRemoteParentDirectories(settings, remotePath);
            PreallocateRemoteFile(settings, remotePath, totalLength);

            await TransferChunksAsync(
                settings,
                state,
                chunkCount,
                streams,
                cancellationToken,
                async (chunkIndex, chunkStart, chunkLength, token) =>
                {
                    await UploadChunkAsync(
                        settings,
                        localPath,
                        remotePath,
                        chunkStart,
                        chunkLength,
                        token);
                });

            DeleteRemoteFileIfExists(settings, sidecarPath);
        }

        private static async Task TransferChunksAsync(
            AppSettings settings,
            TransferState state,
            int chunkCount,
            int streams,
            CancellationToken cancellationToken,
            Func<int, long, long, CancellationToken, Task> transferChunk)
        {
            HashSet<int> completed = state.CompletedChunks.ToHashSet();
            List<int> pending = Enumerable
                .Range(0, chunkCount)
                .Where(index => !completed.Contains(index))
                .ToList();

            object lockObject = new object();

            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = streams,
                    CancellationToken = cancellationToken
                },
                async (chunkIndex, token) =>
                {
                    long chunkStart = (long)chunkIndex * ChunkSize;
                    long chunkLength = GetChunkLength(state.TotalLength, chunkIndex);

                    await transferChunk(chunkIndex, chunkStart, chunkLength, token);

                    lock (lockObject)
                    {
                        if (!state.CompletedChunks.Contains(chunkIndex))
                        {
                            state.CompletedChunks.Add(chunkIndex);
                        }

                        SaveState(GetSidecarPathForState(state), state);
                    }
                });
        }

        private static async Task DownloadChunkAsync(
            AppSettings settings,
            string remotePath,
            string localPath,
            long offset,
            long length,
            CancellationToken cancellationToken)
        {
            using SftpClient client = CreateClient(settings);
            client.Connect();

            try
            {
                await using SftpFileStream remoteStream = client.OpenRead(remotePath);
                await using FileStream localStream = new(
                    localPath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read);

                remoteStream.Seek(offset, SeekOrigin.Begin);
                localStream.Seek(offset, SeekOrigin.Begin);

                byte[] buffer = new byte[128 * 1024];
                long remaining = length;

                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int read = remoteStream.Read(
                        buffer,
                        0,
                        (int)Math.Min(buffer.Length, remaining));

                    if (read <= 0)
                    {
                        throw new IOException(
                            $"Remote stream ended early while downloading '{remotePath}'.");
                    }

                    await localStream.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);

                    remaining -= read;
                }

                await localStream.FlushAsync(cancellationToken);
            }
            finally
            {
                client.Disconnect();
                client.Dispose();
            }
        }

        private static async Task UploadChunkAsync(
            AppSettings settings,
            string localPath,
            string remotePath,
            long offset,
            long length,
            CancellationToken cancellationToken)
        {
            using SftpClient client = CreateClient(settings);
            client.Connect();

            try
            {
                await using FileStream localStream = new(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);

                await using SftpFileStream remoteStream = client.Open(
                    remotePath,
                    FileMode.Open,
                    FileAccess.Write);

                localStream.Seek(offset, SeekOrigin.Begin);
                remoteStream.Seek(offset, SeekOrigin.Begin);

                byte[] buffer = new byte[128 * 1024];
                long remaining = length;

                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int read = localStream.Read(
                        buffer,
                        0,
                        (int)Math.Min(buffer.Length, remaining));

                    if (read <= 0)
                    {
                        break;
                    }

                    await remoteStream.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);

                    remaining -= read;
                }

                await remoteStream.FlushAsync(cancellationToken);
            }
            finally
            {
                client.Disconnect();
                client.Dispose();
            }
        }

        private static void PreallocateRemoteFile(
            AppSettings settings,
            string remotePath,
            long totalLength)
        {
            using SftpClient client = CreateClient(settings);
            client.Connect();

            try
            {
                using SftpFileStream stream = client.Open(
                    remotePath,
                    FileMode.Create,
                    FileAccess.Write);

                stream.SetLength(totalLength);
                stream.Flush();
            }
            finally
            {
                client.Disconnect();
                client.Dispose();
            }
        }

        private static void CreateRemoteParentDirectories(
            AppSettings settings,
            string remotePath)
        {
            string? directory = Path.GetDirectoryName(
                remotePath.Replace('/', Path.DirectorySeparatorChar));

            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            string remoteDirectory = directory
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace("\\", "/");

            if (!remoteDirectory.StartsWith('/'))
            {
                remoteDirectory = "/" + remoteDirectory;
            }

            using SftpClient client = CreateClient(settings);
            client.Connect();

            try
            {
                string current = "/";

                foreach (string segment in remoteDirectory.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    current += segment + "/";

                    if (!client.Exists(current))
                    {
                        client.CreateDirectory(current);
                    }
                }
            }
            finally
            {
                client.Disconnect();
                client.Dispose();
            }
        }

        private static void DeleteRemoteFileIfExists(
            AppSettings settings,
            string remotePath)
        {
            using SftpClient client = CreateClient(settings);
            client.Connect();

            try
            {
                if (!client.Exists(remotePath))
                {
                    return;
                }

                SftpFileAttributes attributes = client.GetAttributes(remotePath);

                if (!attributes.IsDirectory)
                {
                    client.DeleteFile(remotePath);
                }
            }
            finally
            {
                client.Disconnect();
                client.Dispose();
            }
        }

        private static SftpClient CreateClient(AppSettings settings)
        {
            return new SftpClient(
                settings.Host,
                settings.Port,
                settings.Username,
                settings.Password);
        }

        private static int GetChunkCount(long totalLength)
        {
            if (totalLength <= 0)
            {
                return 1;
            }

            return (int)Math.Ceiling(totalLength / (double)ChunkSize);
        }

        private static long GetChunkLength(long totalLength, int chunkIndex)
        {
            long start = (long)chunkIndex * ChunkSize;
            long remaining = totalLength - start;

            return remaining <= 0
                ? 0
                : Math.Min(remaining, ChunkSize);
        }

        private static TransferState LoadOrCreateState(
            string sidecarPath,
            string transferId,
            long totalLength,
            int chunkCount)
        {
            if (File.Exists(sidecarPath))
            {
                try
                {
                    string json = File.ReadAllText(sidecarPath);
                    TransferState? state = JsonSerializer.Deserialize<TransferState>(json);

                    if (state != null &&
                        state.TransferId == transferId &&
                        state.TotalLength == totalLength &&
                        state.ChunkSize == ChunkSize)
                    {
                        state.CompletedChunks = state.CompletedChunks
                            .Where(index => index >= 0 && index < chunkCount)
                            .Distinct()
                            .ToList();

                        return state;
                    }
                }
                catch
                {
                    // A corrupt or incompatible sidecar is recreated below.
                }
            }

            TransferState newState = new TransferState
            {
                TransferId = transferId,
                TotalLength = totalLength,
                ChunkSize = ChunkSize
            };

            SaveState(sidecarPath, newState);
            return newState;
        }

        private static void SaveState(string sidecarPath, TransferState state)
        {
            string? directory = Path.GetDirectoryName(sidecarPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(
                state,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(sidecarPath, json);
        }

        private static string GetSidecarPathForState(TransferState state)
        {
            // The state object does not retain its sidecar path, so this helper
            // is intentionally not used by the current implementation.
            throw new NotSupportedException(
                "Use GetDownloadSidecarPath or GetUploadSidecarPath instead.");
        }

        public static string GetSidecarPath(string path)
        {
            return path + ".streamzilla-progress";
        }
    }
}

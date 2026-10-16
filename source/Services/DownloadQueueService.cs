using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamZilla.Models;

namespace StreamZilla.Services;

public static class DownloadQueueService
{
    private static readonly string QueueDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamZilla", "queues");

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };
    private static readonly object SaveLock = new object();

    public static string GetConnectionKey(string host, int port, string username)
    {
        return $"{host.Trim().ToLowerInvariant()}|{port}|{username.Trim().ToLowerInvariant()}";
    }

    public static void Save(string connectionKey, IEnumerable<DownloadEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(QueueDirectory);

            List<DownloadQueueEntry> pending = entries
                .Where(x => !x.IsFinished)
                .Select(x => new DownloadQueueEntry
                {
                    Id = x.Id,
                    FileName = x.FileName,
                    RemotePath = x.RemotePath,
                    LocalPath = x.LocalPath,
                    TotalLength = x.TotalLength,
                    CompletedLength = x.CompletedLength,
                    ChunkBytes = x.ChunkBytes,
                    Direction = x.Direction
                })
                .ToList();

            string json = JsonSerializer.Serialize(pending, JsonOptions);
            byte[] plain = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

            lock (SaveLock)
            {
                File.WriteAllBytes(GetQueueFilePath(connectionKey), encrypted);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to save download queue: {ex}");
        }
    }

    public static List<DownloadEntry> Load(string connectionKey)
    {
        List<DownloadEntry> result = new List<DownloadEntry>();

        try
        {
            string path = GetQueueFilePath(connectionKey);

            if (!File.Exists(path))
            {
                return result;
            }

            byte[] encrypted = File.ReadAllBytes(path);
            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);

            List<DownloadQueueEntry>? entries = JsonSerializer.Deserialize<List<DownloadQueueEntry>>(
                Encoding.UTF8.GetString(plain), JsonOptions);

            if (entries == null)
            {
                return result;
            }

            foreach (DownloadQueueEntry queueEntry in entries)
            {
                DownloadEntry entry = new DownloadEntry
                {
                    Id = queueEntry.Id,
                    FileName = queueEntry.FileName,
                    RemotePath = queueEntry.RemotePath,
                    LocalPath = queueEntry.LocalPath,
                    TotalLength = queueEntry.TotalLength,
                    ChunkBytes = queueEntry.ChunkBytes ?? Array.Empty<long>(),
                    Status = "Paused",
                    Direction = queueEntry.Direction
                };

                entry.CompletedLength = queueEntry.Direction == TransferDirection.Upload
                    ? Math.Min(Math.Max(queueEntry.CompletedLength, 0), entry.TotalLength)
                    : CalculateCompletedLength(entry);

                result.Add(entry);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to load download queue: {ex}");
        }

        return result;
    }

    public static void Delete(string connectionKey)
    {
        try
        {
            string path = GetQueueFilePath(connectionKey);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to delete download queue: {ex}");
        }
    }

    private static long CalculateCompletedLength(DownloadEntry entry)
    {
        if (entry.TotalLength <= 0 || entry.ChunkBytes.Length == 0)
        {
            return 0;
        }

        int chunkCount = entry.ChunkBytes.Length;
        long chunkSize = entry.TotalLength / chunkCount;
        long completed = 0;

        for (int i = 0; i < chunkCount; i++)
        {
            long chunkLength = i == chunkCount - 1
                ? entry.TotalLength - (chunkSize * (chunkCount - 1))
                : chunkSize;

            completed += Math.Min(entry.ChunkBytes[i], chunkLength);
        }

        return Math.Min(completed, entry.TotalLength);
    }

    private static string GetQueueFilePath(string connectionKey)
    {
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(connectionKey));
            return Path.Combine(QueueDirectory, Convert.ToHexString(hash) + ".bin");
        }
    }
}

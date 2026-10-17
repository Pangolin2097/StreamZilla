using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamZilla.Models;

namespace StreamZilla.Services;

public static class TransferQueueService
{
    private static readonly string QueueDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamZilla", "queues");

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };
    private static readonly object SaveLock = new object();

    public static string GetConnectionKey(string host, int port, string username)
    {
        return $"{host.Trim().ToLowerInvariant()}|{port}|{username.Trim().ToLowerInvariant()}";
    }

    public static void Save(string connectionKey, IEnumerable<TransferEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(QueueDirectory);

            List<TransferQueueEntry> pending = entries
                .Where(x => !x.IsFinished)
                .Select(x => new TransferQueueEntry
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
            LogService.Error($"Failed to save transfer queue: {ex}");
        }
    }

    public static List<TransferEntry> Load(string connectionKey)
    {
        List<TransferEntry> result = new List<TransferEntry>();

        try
        {
            string path = GetQueueFilePath(connectionKey);

            if (!File.Exists(path))
            {
                return result;
            }

            byte[] encrypted = File.ReadAllBytes(path);
            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);

            List<TransferQueueEntry>? entries = JsonSerializer.Deserialize<List<TransferQueueEntry>>(
                Encoding.UTF8.GetString(plain), JsonOptions);

            if (entries == null)
            {
                return result;
            }

            foreach (TransferQueueEntry queueEntry in entries)
            {
                TransferEntry entry = new TransferEntry
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
            LogService.Error($"Failed to load transfer queue: {ex}");
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
            LogService.Error($"Failed to delete transfer queue: {ex}");
        }
    }

    private static long CalculateCompletedLength(TransferEntry entry)
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

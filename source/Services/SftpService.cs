using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using StreamZilla.Models;

namespace StreamZilla.Services;

public sealed class SftpService : IDisposable
{
    public const string PermissionDeniedMessage = "Permission denied on the remote server.";
    public const string SidecarSuffix = ".streamzilla-progress";

    private SftpClient? _client;
    private SftpClient? _listClient;
    private readonly SemaphoreSlim _listGate = new(1, 1);
    private byte[]? _lastHostKey;
    private readonly Dictionary<int, string> _uidCache = new();
    private readonly Dictionary<int, string> _gidCache = new();
    private readonly object _uidCacheLock = new();
    private readonly object _gidCacheLock = new();
    private AppSettings? _settings;

    public bool IsConnected => _client != null && _client.IsConnected;

    public void Connect(AppSettings settings)
    {
        Disconnect();

        PasswordAuthenticationMethod auth = new(settings.Username, settings.Password);
        ConnectionInfo info = new(settings.Host, settings.Port, settings.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        _client = new SftpClient(info);
        _lastHostKey = null;
        _settings = settings;

        _client.HostKeyReceived += (_, e) => _lastHostKey = e.HostKey;
        _client.Connect();

        if (!_client.IsConnected)
        {
            throw new InvalidOperationException("Could not connect to SFTP server.");
        }

        _listClient = CreateListClient(settings);
        _listClient.Connect();
    }

    private static SftpClient CreateListClient(AppSettings settings)
    {
        PasswordAuthenticationMethod auth = new(settings.Username, settings.Password);
        ConnectionInfo info = new(settings.Host, settings.Port, settings.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        return new SftpClient(info)
        {
            OperationTimeout = TimeSpan.FromSeconds(30),
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        };
    }

    private void EnsureListConnected()
    {
        if (_settings == null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        if (_listClient != null && _listClient.IsConnected)
        {
            return;
        }

        _listClient?.Dispose();
        _listClient = CreateListClient(_settings);
        _listClient.Connect();
    }

    public string GetHostKeyFingerprint()
    {
        if (_lastHostKey == null || _lastHostKey.Length == 0)
        {
            return "";
        }

        using SHA256 sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(_lastHostKey));
    }

    public List<RemoteEntry> ListDirectory(string path)
    {
        EnsureListConnected();
        _listGate.Wait();

        try
        {
            List<RemoteEntry> items = new();
            HashSet<int> uids = new();
            HashSet<int> gids = new();

            foreach (SftpFile file in _listClient!.ListDirectory(path))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                uids.Add(file.UserId);
                gids.Add(file.GroupId);
                items.Add(CreateRemoteEntry(file));
            }

            Dictionary<int, string> owners = ResolveUids(uids);
            Dictionary<int, string> groups = ResolveGids(gids);

            foreach (RemoteEntry item in items)
            {
                if (owners.TryGetValue(int.Parse(item.Owner), out string? owner))
                {
                    item.Owner = owner;
                }

                if (groups.TryGetValue(int.Parse(item.Group), out string? group))
                {
                    item.Group = group;
                }
            }

            return items
                .OrderBy(x => !x.IsDirectory)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _listGate.Release();
        }
    }

    public List<RemoteEntry> ListDirectoryIsolated(AppSettings settings, string path) =>
        ListDirectory(path);

    public List<RemoteEntry> ListFilesRecursive(string directory)
    {
        EnsureListConnected();
        _listGate.Wait();

        try
        {
            List<RemoteEntry> result = new();
            Walk(directory, result);
            return result;
        }
        finally
        {
            _listGate.Release();
        }
    }

    public List<RemoteEntry> ListFilesRecursiveIsolated(AppSettings settings, string directory) =>
        ListFilesRecursive(directory);

    private void Walk(string directory, List<RemoteEntry> output)
    {
        HashSet<int> uids = new();
        HashSet<int> gids = new();
        List<RemoteEntry> batch = new();

        foreach (SftpFile file in _listClient!.ListDirectory(directory))
        {
            if (file.Name is "." or "..")
            {
                continue;
            }

            if (file.IsDirectory)
            {
                Walk(file.FullName, output);
                continue;
            }

            uids.Add(file.UserId);
            gids.Add(file.GroupId);
            batch.Add(CreateRemoteEntry(file));
        }

        Dictionary<int, string> owners = ResolveUids(uids);
        Dictionary<int, string> groups = ResolveGids(gids);

        foreach (RemoteEntry item in batch)
        {
            if (owners.TryGetValue(int.Parse(item.Owner), out string? owner))
            {
                item.Owner = owner;
            }

            if (groups.TryGetValue(int.Parse(item.Group), out string? group))
            {
                item.Group = group;
            }

            output.Add(item);
        }
    }

    private static RemoteEntry CreateRemoteEntry(SftpFile file)
    {
        return new RemoteEntry
        {
            Name = file.Name,
            FullPath = file.FullName,
            IsDirectory = file.IsDirectory,
            Size = file.Length,
            Owner = file.UserId.ToString(),
            Group = file.GroupId.ToString(),
            Permissions = FormatPermissions(file.Attributes),
            Modified = file.LastWriteTime,
            Created = file.LastAccessTime
        };
    }

    public Task UploadFileAsync(
        string localPath,
        string remotePath,
        Action<long>? progress = null,
        CancellationToken token = default,
        long startOffset = 0)
    {
        return Task.Run(async () =>
        {
            EnsureConnected();

            if (!File.Exists(localPath))
            {
                throw new FileNotFoundException("The local file does not exist.", localPath);
            }

            long totalLength = new FileInfo(localPath).Length;
            string remoteDirectory = Path.GetDirectoryName(remotePath) ?? "/";
            string sidecarPath = remotePath + SidecarSuffix;

            if (remoteDirectory.Length > 0 && remoteDirectory != "/")
            {
                try
                {
                    if (!_client!.Exists(remoteDirectory))
                    {
                        CreateDirectory(remoteDirectory);
                    }
                }
                catch (SftpPermissionDeniedException)
                {
                }
            }

            token.ThrowIfCancellationRequested();

            long sidecarOffset = ReadUploadSidecarOffset(sidecarPath);
            long remoteSize = GetRemoteFileSize(remotePath);
            long resumeOffset = sidecarOffset > 0
                ? Math.Min(sidecarOffset, remoteSize)
                : 0;

            if (resumeOffset == 0 && remoteSize > 0)
            {
                _client!.DeleteFile(remotePath);
            }

            WriteUploadSidecar(sidecarPath, totalLength, resumeOffset);
            progress?.Invoke(resumeOffset);

            using FileStream source = new(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            if (resumeOffset > 0 && resumeOffset < totalLength)
            {
                source.Seek(resumeOffset, SeekOrigin.Begin);
            }

            SftpFileStream destination = _client!.OpenWrite(remotePath);

            if (resumeOffset > 0)
            {
                destination.Seek(resumeOffset, SeekOrigin.Begin);
            }

            using (destination)
            {
                byte[] buffer = new byte[1024 * 1024];
                long written = resumeOffset;
                long lastSidecarUpdate = 0;

                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    int read = source.Read(buffer, 0, buffer.Length);

                    if (read <= 0)
                    {
                        break;
                    }

                    destination.Write(buffer, 0, read);
                    destination.Flush();
                    written += read;
                    progress?.Invoke(written);

                    if (written - lastSidecarUpdate >= 1024 * 1024 || written >= totalLength)
                    {
                        lastSidecarUpdate = written;
                        WriteUploadSidecar(sidecarPath, totalLength, written);
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            DeleteRemoteFileIfExists(sidecarPath);

            LogService.Info($"Uploaded file: {localPath} -> {remotePath} ({totalLength} bytes).");
        }, token);
    }

    private long ReadUploadSidecarOffset(string sidecarPath)
    {
        try
        {
            if (!_client!.Exists(sidecarPath))
            {
                return 0;
            }

            TransferSidecar? sidecar = JsonSerializer.Deserialize<TransferSidecar>(
                _client.ReadAllText(sidecarPath));

            return sidecar?.CompletedLength ?? 0;
        }
        catch (Exception ex)
        {
            LogService.Warn($"Could not read upload sidecar {sidecarPath}: {ex.Message}");
            return 0;
        }
    }

    private void WriteUploadSidecar(string sidecarPath, long totalLength, long completedLength)
    {
        try
        {
            TransferSidecar sidecar = new()
            {
                Direction = "Upload",
                TotalLength = totalLength,
                CompletedLength = completedLength,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _client!.WriteAllText(sidecarPath, JsonSerializer.Serialize(sidecar));
        }
        catch (Exception ex)
        {
            LogService.Warn($"Could not write upload sidecar {sidecarPath}: {ex.Message}");
        }
    }

    private void DeleteRemoteFileIfExists(string remotePath)
    {
        try
        {
            if (_client!.Exists(remotePath))
            {
                _client.DeleteFile(remotePath);
            }
        }
        catch (SftpPathNotFoundException)
        {
        }
    }

    public long GetRemoteFileSize(string remotePath)
    {
        EnsureConnected();

        try
        {
            SftpFileAttributes attributes = _client!.GetAttributes(remotePath);
            return attributes.IsDirectory ? 0 : attributes.Size;
        }
        catch (SftpPathNotFoundException)
        {
            return 0;
        }
    }

    public void CreateDirectory(string remotePath)
    {
        EnsureConnected();

        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new ArgumentException("A remote directory path is required.", nameof(remotePath));
        }

        if (IsRemoteDirectory(remotePath))
        {
            return;
        }

        List<string> parts = remotePath
            .Replace('\\', '/')
            .Split('/')
            .Where(x => x.Length > 0)
            .ToList();

        string current = "/";

        foreach (string part in parts)
        {
            string candidate = current + part;

            if (IsRemoteDirectory(candidate))
            {
                current = candidate;
                continue;
            }

            try
            {
                _client!.CreateDirectory(candidate);
                LogService.Info($"Created remote directory: {candidate}");
            }
            catch (SftpPermissionDeniedException)
            {
                current = candidate;
                continue;
            }

            current = candidate;
        }
    }

    public void DeleteFile(string remotePath)
    {
        EnsureConnected();
        _client!.DeleteFile(remotePath);
        LogService.Info($"Deleted remote file: {remotePath}");
    }

    public void DeleteDirectory(string remotePath)
    {
        EnsureConnected();

        if (IsRemoteDirectory(remotePath))
        {
            DeleteDirectoryRecursive(remotePath);
            return;
        }

        _client!.DeleteFile(remotePath);
        LogService.Info($"Deleted remote file: {remotePath}");
    }

    public bool IsRemoteDirectory(string remotePath)
    {
        EnsureConnected();

        try
        {
            return _client!.GetAttributes(remotePath).IsDirectory;
        }
        catch (SftpPathNotFoundException)
        {
            return false;
        }
        catch (SftpPermissionDeniedException)
        {
            return false;
        }
    }

    private void DeleteDirectoryRecursive(string remotePath)
    {
        EnsureConnected();

        foreach (SftpFile child in _client!.ListDirectory(remotePath))
        {
            if (child.Name is "." or "..")
            {
                continue;
            }

            if (child.IsDirectory)
            {
                DeleteDirectoryRecursive(child.FullName);
            }
            else
            {
                _client.DeleteFile(child.FullName);
                LogService.Info($"Deleted remote file: {child.FullName}");
            }
        }

        _client.DeleteDirectory(remotePath);
        LogService.Info($"Deleted remote directory: {remotePath}");
    }

    private static string FormatPermissions(SftpFileAttributes attributes)
    {
        string result = attributes.IsDirectory ? "d" : "-";
        result += attributes.OwnerCanRead ? "r" : "-";
        result += attributes.OwnerCanWrite ? "w" : "-";
        result += attributes.OwnerCanExecute ? "x" : "-";
        result += attributes.GroupCanRead ? "r" : "-";
        result += attributes.GroupCanWrite ? "w" : "-";
        result += attributes.GroupCanExecute ? "x" : "-";
        result += attributes.OthersCanRead ? "r" : "-";
        result += attributes.OthersCanWrite ? "w" : "-";
        result += attributes.OthersCanExecute ? "x" : "-";
        return result;
    }

    private Dictionary<int, string> ResolveUids(ICollection<int> ids)
    {
        return ResolveIds(ids, _uidCache, _uidCacheLock, "id -un");
    }

    private Dictionary<int, string> ResolveGids(ICollection<int> ids)
    {
        return ResolveIds(ids, _gidCache, _gidCacheLock, "id -gn");
    }

    private Dictionary<int, string> ResolveIds(
        ICollection<int> ids,
        Dictionary<int, string> cache,
        object cacheLock,
        string command)
    {
        Dictionary<int, string> result = new();

        lock (cacheLock)
        {
            foreach (int id in ids)
            {
                if (cache.TryGetValue(id, out string? name))
                {
                    result[id] = name;
                }
            }
        }

        List<int> missing = ids.Where(x => !result.ContainsKey(x)).ToList();

        if (missing.Count == 0 || _settings == null)
        {
            return result;
        }

        string script = string.Join(";", missing.Select(x => $"{command} {x}"));

        try
        {
            PasswordAuthenticationMethod auth = new(_settings.Username, _settings.Password);
            ConnectionInfo info = new(_settings.Host, _settings.Port, _settings.Username, auth)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            using SshClient ssh = new(info);
            ssh.Connect();

            using Renci.SshNet.SshCommand response = ssh.RunCommand(script);
            string[] lines = response.Result.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < missing.Count && i < lines.Length; i++)
            {
                string name = lines[i].Trim();
                result[missing[i]] = name;

                lock (cacheLock)
                {
                    cache[missing[i]] = name;
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private void EnsureConnected()
    {
        if (_client == null || !_client.IsConnected)
        {
            throw new InvalidOperationException("Not connected to SFTP server.");
        }
    }

    public void Disconnect()
    {
        if (_listClient != null)
        {
            try
            {
                _listClient.Disconnect();
                _listClient.Dispose();
            }
            catch
            {
            }

            _listClient = null;
        }

        if (_client != null)
        {
            try
            {
                _client.Disconnect();
                _client.Dispose();
            }
            catch
            {
            }

            _client = null;
        }

        _lastHostKey = null;
        LogService.Info("Disconnected from SFTP.");
    }

    public void Dispose() => Disconnect();

    private sealed class TransferSidecar
    {
        public string TransferId { get; set; } = "";
        public string Direction { get; set; } = "";
        public long TotalLength { get; set; }
        public long CompletedLength { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}

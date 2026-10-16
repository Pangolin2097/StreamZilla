using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using StreamZilla.Models;

namespace StreamZilla.Services;

public sealed class SftpService : IDisposable
{
    public const string PermissionDeniedMessage = "Permission denied on the remote server.";
    private const string UploadTempSuffix = ".streamzilla-upload";

    private SftpClient? _client;
    private byte[]? _lastHostKey;
    private readonly Dictionary<int, string> _uidCache = new();
    private readonly object _uidCacheLock = new();
    private readonly Dictionary<int, string> _gidCache = new();
    private readonly object _gidCacheLock = new();
    private AppSettings? _settings;

    public bool IsConnected
    {
        get { return _client != null && _client.IsConnected; }
    }

    public void Connect(AppSettings settings)
    {
        Disconnect();

        AuthenticationMethod auth = new PasswordAuthenticationMethod(settings.Username, settings.Password);
        ConnectionInfo info = new ConnectionInfo(settings.Host, settings.Port, settings.Username, auth)
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

        LogService.Info($"Connected to SFTP {settings.Host}:{settings.Port} as {settings.Username}.");
    }

    public string GetHostKeyFingerprint()
    {
        if (_lastHostKey == null || _lastHostKey.Length == 0)
        {
            return "";
        }

        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(_lastHostKey);
        return Convert.ToBase64String(hash);
    }

    public List<RemoteEntry> ListDirectory(string path)
    {
        EnsureConnected();

        List<RemoteEntry> items = new List<RemoteEntry>();
        HashSet<int> uidsToResolve = new();
        HashSet<int> gidsToResolve = new();

        foreach (SftpFile file in _client!.ListDirectory(path))
        {
            if (file.Name == "." || file.Name == "..")
            {
                continue;
            }

            uidsToResolve.Add(file.UserId);
            gidsToResolve.Add(file.GroupId);
            items.Add(CreateRemoteEntry(file));
        }

        Dictionary<int, string> resolvedUids = ResolveUids(uidsToResolve);
        Dictionary<int, string> resolvedGids = ResolveGids(gidsToResolve);

        foreach (RemoteEntry entry in items)
        {
            if (resolvedUids.TryGetValue(int.Parse(entry.Owner), out string? name))
            {
                entry.Owner = name;
            }

            if (resolvedGids.TryGetValue(int.Parse(entry.Group), out string? group))
            {
                entry.Group = group;
            }
        }

        return items
            .OrderBy(x => !x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<RemoteEntry> ListDirectoryIsolated(AppSettings settings, string path)
    {
        SftpClient client = ParallelSftpDownloadService.CreateClient(settings);

        try
        {
            client.Connect();

            List<RemoteEntry> items = new List<RemoteEntry>();

            foreach (SftpFile file in client.ListDirectory(path))
            {
                if (file.Name == "." || file.Name == "..")
                {
                    continue;
                }

                items.Add(CreateRemoteEntry(file));
            }

            return items
                .OrderBy(x => !x.IsDirectory)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
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

    public List<RemoteEntry> ListFilesRecursive(string directory)
    {
        EnsureConnected();

        List<RemoteEntry> result = new List<RemoteEntry>();
        Walk(directory, result);
        return result;
    }

    public List<RemoteEntry> ListFilesRecursiveIsolated(AppSettings settings, string directory)
    {
        SftpClient client = ParallelSftpDownloadService.CreateClient(settings);

        try
        {
            client.Connect();

            List<RemoteEntry> result = new List<RemoteEntry>();
            WalkWithClient(client, directory, result);
            return result;
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

    private void Walk(string directory, List<RemoteEntry> output)
    {
        EnsureConnected();

        HashSet<int> uidsToResolve = new();
        HashSet<int> gidsToResolve = new();
        List<RemoteEntry> batch = new List<RemoteEntry>();

        foreach (SftpFile file in _client!.ListDirectory(directory))
        {
            if (file.Name == "." || file.Name == "..")
            {
                continue;
            }

            if (file.IsDirectory)
            {
                Walk(file.FullName, output);
            }
            else
            {
                uidsToResolve.Add(file.UserId);
                gidsToResolve.Add(file.GroupId);
                batch.Add(CreateRemoteEntry(file));
            }
        }

        Dictionary<int, string> resolvedUids = ResolveUids(uidsToResolve);
        Dictionary<int, string> resolvedGids = ResolveGids(gidsToResolve);

        foreach (RemoteEntry entry in batch)
        {
            if (resolvedUids.TryGetValue(int.Parse(entry.Owner), out string? name))
            {
                entry.Owner = name;
            }

            if (resolvedGids.TryGetValue(int.Parse(entry.Group), out string? group))
            {
                entry.Group = group;
            }

            output.Add(entry);
        }
    }

    private static void WalkWithClient(SftpClient client, string directory, List<RemoteEntry> output)
    {
        foreach (SftpFile file in client.ListDirectory(directory))
        {
            if (file.Name == "." || file.Name == "..")
            {
                continue;
            }

            if (file.IsDirectory)
            {
                WalkWithClient(client, file.FullName, output);
            }
            else
            {
                output.Add(CreateRemoteEntry(file));
            }
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
            LastModified = file.LastWriteTime,
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
            string tempPath = remotePath + UploadTempSuffix;

            if (remoteDirectory.Length > 0 && !remoteDirectory.Equals("/", StringComparison.Ordinal))
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

            long resumeOffset = GetUploadResumeOffset(tempPath);
            progress?.Invoke(resumeOffset);

            using (FileStream source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (resumeOffset > 0 && resumeOffset < totalLength)
                {
                    source.Seek(resumeOffset, SeekOrigin.Begin);
                }

                SftpFileStream destination = _client!.OpenWrite(tempPath);

                if (resumeOffset > 0)
                {
                    destination.Seek(resumeOffset, SeekOrigin.Begin);
                }

                using (destination)
                {
                    byte[] buffer = new byte[1024 * 1024];
                    long written = resumeOffset;

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
                    }
                }
            }

            token.ThrowIfCancellationRequested();

            try
            {
                if (_client!.Exists(remotePath))
                {
                    _client.DeleteFile(remotePath);
                }

                _client.RenameFile(tempPath, remotePath);
            }
            catch (SftpPathNotFoundException)
            {
            }

            LogService.Info($"Uploaded file: {localPath} -> {remotePath} ({totalLength} bytes).");
        }, token);
    }

    private long GetUploadResumeOffset(string tempPath)
    {
        try
        {
            SftpFileAttributes attributes = _client!.GetAttributes(tempPath);
            return attributes.IsDirectory ? 0 : attributes.Size;
        }
        catch (SftpPathNotFoundException)
        {
            return 0;
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
            SftpFileAttributes attributes = _client!.GetAttributes(remotePath);
            return attributes.IsDirectory;
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
            if (child.Name == "." || child.Name == "..")
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

    private Dictionary<int, string> ResolveUids(ICollection<int> uids)
    {
        Dictionary<int, string> result = new();

        lock (_uidCacheLock)
        {
            foreach (int uid in uids)
            {
                if (_uidCache.TryGetValue(uid, out string? cached))
                {
                    result[uid] = cached;
                }
            }
        }

        List<int> missing = uids.Where(uid => !result.ContainsKey(uid)).ToList();

        if (missing.Count == 0 || _settings == null)
        {
            return result;
        }

        string command = string.Join(";", missing.Select(uid => $"id -un {uid}"));

        try
        {
            AuthenticationMethod auth = new PasswordAuthenticationMethod(_settings.Username, _settings.Password);
            ConnectionInfo info = new ConnectionInfo(_settings.Host, _settings.Port, _settings.Username, auth)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            using SshClient ssh = new SshClient(info);
            ssh.Connect();

            using var response = ssh.RunCommand(command);
            string[] lines = response.Result.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < missing.Count && i < lines.Length; i++)
            {
                string name = lines[i].Trim();
                result[missing[i]] = name;

                lock (_uidCacheLock)
                {
                    _uidCache[missing[i]] = name;
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private Dictionary<int, string> ResolveGids(ICollection<int> gids)
    {
        Dictionary<int, string> result = new();

        lock (_gidCacheLock)
        {
            foreach (int gid in gids)
            {
                if (_gidCache.TryGetValue(gid, out string? cached))
                {
                    result[gid] = cached;
                }
            }
        }

        List<int> missing = gids.Where(gid => !result.ContainsKey(gid)).ToList();

        if (missing.Count == 0 || _settings == null)
        {
            return result;
        }

        string command = string.Join(";", missing.Select(gid => $"id -gn {gid}"));

        try
        {
            AuthenticationMethod auth = new PasswordAuthenticationMethod(_settings.Username, _settings.Password);
            ConnectionInfo info = new ConnectionInfo(_settings.Host, _settings.Port, _settings.Username, auth)
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            using SshClient ssh = new SshClient(info);
            ssh.Connect();

            using var response = ssh.RunCommand(command);
            string[] lines = response.Result.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < missing.Count && i < lines.Length; i++)
            {
                string name = lines[i].Trim();
                result[missing[i]] = name;

                lock (_gidCacheLock)
                {
                    _gidCache[missing[i]] = name;
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
        if (_client == null)
        {
            return;
        }

        try
        {
            _client.Disconnect();
        }
        catch
        {
        }

        try
        {
            _client.Dispose();
        }
        catch
        {
        }

        _client = null;
        _lastHostKey = null;
        LogService.Info("Disconnected from SFTP.");
    }

    public void Dispose()
    {
        Disconnect();
    }
}

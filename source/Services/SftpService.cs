using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using StreamZilla.Models;

namespace StreamZilla.Services;

public sealed class SftpService : IDisposable
{
    private SftpClient? _client;
    private byte[]? _lastHostKey;

    public bool IsConnected
    {
        get
        {
            return _client != null && _client.IsConnected;
        }
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

        _client.HostKeyReceived += (_, e) =>
        {
            _lastHostKey = e.HostKey;
        };

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

        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(_lastHostKey);
            return Convert.ToBase64String(hash);
        }
    }

    public List<RemoteEntry> ListDirectory(string path)
    {
        EnsureConnected();

        List<RemoteEntry> items = new List<RemoteEntry>();

        foreach (SftpFile file in _client!.ListDirectory(path))
        {
            if (file.Name == "." || file.Name == "..")
            {
                continue;
            }

            RemoteEntry entry = new RemoteEntry
            {
                Name = file.Name,
                FullPath = file.FullName,
                IsDirectory = file.IsDirectory,
                Size = file.Length
            };

            items.Add(entry);
        }

        return items
            .OrderBy(x => !x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<RemoteEntry> ListFilesRecursive(string directory)
    {
        EnsureConnected();

        List<RemoteEntry> result = new List<RemoteEntry>();
        Walk(directory, result);
        return result;
    }

    // Uses its own dedicated connection so directory scans never contend with the
    // main connection (used by the tree refresh timer) or with each other.
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
                RemoteEntry entry = new RemoteEntry
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    Size = file.Length
                };

                output.Add(entry);
            }
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
                RemoteEntry entry = new RemoteEntry
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false,
                    Size = file.Length
                };

                output.Add(entry);
            }
        }
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

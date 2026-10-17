using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StreamZilla.Commands;
using StreamZilla.Dialogs;
using StreamZilla.Models;
using StreamZilla.Services;

namespace StreamZilla.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly SftpService _sftp = new();
    private readonly IParallelTransferService _transferService = new ParallelSftpTransferService();
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _remoteRefreshTimer = new();
    private readonly Dictionary<string, FileSystemWatcher> _localWatchers = new();
    private readonly List<TransferItemViewModel> _pendingTransfers = new();
    private readonly HashSet<string> _activeTransferIds = new();
    private readonly SemaphoreSlim _queueSaveGate = new(1, 1);

    private readonly AppSettings _settings;
    private string _currentConnectionKey = "";
    private EntryItemViewModel? _selectionAnchor;
    private EntryItemViewModel? _lastLocalFolder;
    private string _maxStreamsDisplay;
    private string _maxFilesDisplay;

    public MainViewModel(IDialogService dialogService)
    {
        _dialogs = dialogService;
        _settings = EncryptedSettingsService.Load();

        _maxStreamsDisplay = Math.Clamp(_settings.ParallelConnections, 1, 8).ToString();
        _maxFilesDisplay = Math.Clamp(_settings.MaxConcurrentTransfers, 1, 8).ToString();

        ConnectCommand = new RelayCommand(async _ => await ConnectAsync(), _ => !IsBusy);
        DisconnectCommand = new RelayCommand(_ => Disconnect());
        RefreshRemoteCommand = new RelayCommand(async _ => await RefreshRemoteAsync(), _ => _sftp.IsConnected);
        PauseAllCommand = new RelayCommand(_ => PauseAll(), _ => Transfers.Any(x => x.CanPause));
        ResumeAllCommand = new RelayCommand(_ => ResumeAll(), _ => Transfers.Any(x => x.CanResume));
        ClearQueueCommand = new RelayCommand(_ => ClearQueue(), _ => Transfers.Count > 0);
        RemoveSelectedCommand = new RelayCommand(
            parameter => RemoveSelected(parameter as IList),
            parameter => parameter is IList list && list.Count > 0);

        PauseTransferCommand = new RelayCommand(
            parameter => { if (parameter is TransferItemViewModel item) { PauseTransfer(item); } },
            parameter => parameter is TransferItemViewModel item && item.CanPause);

        ResumeTransferCommand = new RelayCommand(
            parameter => { if (parameter is TransferItemViewModel item) { ResumeTransfer(item); } },
            parameter => parameter is TransferItemViewModel item && item.CanResume);

        RemoveTransferCommand = new RelayCommand(
            parameter => { if (parameter is TransferItemViewModel item) { RemoveTransfer(item); } });

        NewFolderCommand = new RelayCommand(
            parameter => { if (parameter is EntryItemViewModel item) { NewLocalFolder(item); } });

        DeleteLocalItemsCommand = new RelayCommand(
            parameter => DeleteEntries(parameter as IList, isRemote: false));

        UploadCommand = new RelayCommand(
            parameter => { if (parameter is EntryItemViewModel item) { UploadToRemote(item); } },
            _ => _sftp.IsConnected);

        NewRemoteFolderCommand = new RelayCommand(
            parameter => { if (parameter is EntryItemViewModel item) { NewRemoteFolder(item); } },
            _ => _sftp.IsConnected);

        DeleteRemoteItemsCommand = new RelayCommand(
            parameter => DeleteEntries(parameter as IList, isRemote: true),
            _ => _sftp.IsConnected);

        LoadLocalDrives();

        _pollTimer.Interval = TimeSpan.FromMilliseconds(500);
        _pollTimer.Tick += PollTransfers;
        _pollTimer.Start();

        _remoteRefreshTimer.Interval = TimeSpan.FromSeconds(3);
        _remoteRefreshTimer.Tick += (_, _) => RefreshExpandedRemoteDirectories();
        _remoteRefreshTimer.Start();

        LogService.Info("StreamZilla started.");
    }

    public event Action<EntryItemViewModel>? ScrollToEntryRequested;

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand RefreshRemoteCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ResumeAllCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand PauseTransferCommand { get; }
    public RelayCommand ResumeTransferCommand { get; }
    public RelayCommand RemoveTransferCommand { get; }
    public RelayCommand NewFolderCommand { get; }
    public RelayCommand DeleteLocalItemsCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand NewRemoteFolderCommand { get; }
    public RelayCommand DeleteRemoteItemsCommand { get; }

    public ObservableCollection<TransferItemViewModel> Transfers { get; } = new();
    public ObservableCollection<EntryItemViewModel> LocalRoots { get; } = new();
    public ObservableCollection<EntryItemViewModel> RemoteRoots { get; } = new();

    public bool IsConnected => _sftp.IsConnected;
    public bool IsBusy { get; private set; }

    public string Host
    {
        get => _settings.Host;
        set { _settings.Host = value; OnPropertyChanged(); }
    }

    public string Port
    {
        get => _settings.Port.ToString();
        set
        {
            if (int.TryParse(value, out int port))
            {
                _settings.Port = port;
                OnPropertyChanged();
            }
        }
    }

    public string Username
    {
        get => _settings.Username;
        set { _settings.Username = value; OnPropertyChanged(); }
    }

    public string Password
    {
        get => _settings.Password;
        set { _settings.Password = value; OnPropertyChanged(); }
    }

    public string MaxStreamsPerFile
    {
        get => _maxStreamsDisplay;
        set
        {
            if (int.TryParse(value, out int streams) && streams is >= 1 and <= 8)
            {
                _maxStreamsDisplay = value;
                _settings.ParallelConnections = streams;
                OnPropertyChanged();
            }
        }
    }

    public string MaxConcurrentFiles
    {
        get => _maxFilesDisplay;
        set
        {
            if (int.TryParse(value, out int max) && max is >= 1 and <= 8)
            {
                _maxFilesDisplay = value;
                _settings.MaxConcurrentTransfers = max;
                OnPropertyChanged();
            }
        }
    }

    public EntryItemViewModel? LastLocalFolder
    {
        get => _lastLocalFolder;
        private set => _lastLocalFolder = value;
    }

    public string QueueCountDisplay =>
        $"{Transfers.Count(x => x.IsComplete)}/{Transfers.Count}";

    public string TotalSizeDisplay =>
        $"{FileIconHelper.FormatSize(Transfers.Sum(x => x.CompletedLength))} / " +
        $"{FileIconHelper.FormatSize(Transfers.Sum(x => x.TotalLength))}";

    public string TotalSpeedDisplay
    {
        get
        {
            long speed = Transfers.Sum(x => x.TransferSpeed);

            return speed <= 0
                ? ""
                : speed >= 1_048_576
                    ? $"{speed / 1_048_576.0:F1} MB/s"
                    : $"{speed / 1024.0:F0} KB/s";
        }
    }

    private async Task ConnectAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            SaveSettings();
            _sftp.Connect(_settings);

            if (!VerifyHostKey())
            {
                _sftp.Disconnect();
                return;
            }

            _currentConnectionKey = TransferQueueService.GetConnectionKey(
                _settings.Host,
                _settings.Port,
                _settings.Username);

            RemoteRoots.Clear();
            await LoadRemoteRootAsync();
            LoadPersistedTransfers();

            OnPropertyChanged(nameof(IsConnected));
        }
        catch (Exception ex)
        {
            LogService.Error($"Connection failed: {ex}");
            _dialogs.ShowError("Connection error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool VerifyHostKey()
    {
        string fingerprint = _sftp.GetHostKeyFingerprint();

        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return true;
        }

        string hostKey = $"{_settings.Host.Trim().ToLowerInvariant()}:{_settings.Port}";

        if (_settings.KnownHostKeys.TryGetValue(hostKey, out string? stored) &&
            stored.Equals(fingerprint, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(stored))
        {
            _dialogs.ShowError(
                "SSH host key changed",
                $"The SSH host key for {_settings.Host}:{_settings.Port} has changed.\n\n" +
                $"Stored: {stored}\nCurrent: {fingerprint}\n\n" +
                "If this change is expected, remove the saved host key for this connection.");

            return false;
        }

        if (!_dialogs.ConfirmHostKey(_settings.Host, _settings.Port, fingerprint))
        {
            return false;
        }

        _settings.KnownHostKeys[hostKey] = fingerprint;
        SaveSettings();
        return true;
    }

    public void Disconnect()
    {
        try
        {
            foreach (TransferItemViewModel item in Transfers.ToList())
            {
                if (item.Status is "Active" or "Queued")
                {
                    if (_cancellationTokens.TryGetValue(item.Entry.Id, out CancellationTokenSource? cts))
                    {
                        cts.Cancel();
                    }

                    item.Status = "Paused";
                    item.TransferSpeed = 0;
                }
            }

            // Persist all unfinished transfers before clearing the visible queue.
            SaveQueue();

            Transfers.Clear();
            _pendingTransfers.Clear();

            foreach (CancellationTokenSource cts in _cancellationTokens.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _cancellationTokens.Clear();
            _activeTransferIds.Clear();

            _sftp.Disconnect();
            RemoteRoots.Clear();
            ClearHighlight(RemoteRoots);
            _selectionAnchor = null;

            OnPropertyChanged(nameof(IsConnected));
            LogService.Info("Disconnected. Unfinished transfers were saved as paused.");
        }
        catch (Exception ex)
        {
            LogService.Error($"Disconnect failed: {ex}");
            _dialogs.ShowError("Disconnect error", ex.Message);
        }
    }

    private async Task RefreshRemoteAsync()
    {
        try
        {
            RemoteRoots.Clear();
            await LoadRemoteRootAsync();
        }
        catch (Exception ex)
        {
            LogService.Error($"Refresh failed: {ex}");
            _dialogs.ShowError("Error", ex.Message);
        }
    }

    private async Task LoadRemoteRootAsync()
    {
        RemoteEntry rootEntry = new()
        {
            Name = "",
            FullPath = "/",
            IsDirectory = true
        };

        EntryItemViewModel root = new(rootEntry)
        {
            IsExpanded = true,
            ChildrenLoaded = true
        };

        foreach (RemoteEntry child in _sftp.ListDirectory("/"))
        {
            root.Children.Add(new EntryItemViewModel(child, root));
        }

        RemoteRoots.Add(root);
        RestoreRemoteExpansion(root, GetRemoteExpandedPaths(), 0);

        await ScrollToLastRemoteSelectionAsync();
    }

    private List<string> GetRemoteExpandedPaths()
    {
        return _settings.ExpandedRemotePathsByConnection.TryGetValue(
            _currentConnectionKey,
            out List<string>? paths)
                ? paths
                : new List<string>();
    }

    private void RestoreRemoteExpansion(EntryItemViewModel item, List<string> expandedPaths, int depth)
    {
        if (depth > 20 || !expandedPaths.Contains(item.FullPath))
        {
            return;
        }

        try
        {
            if (!item.ChildrenLoaded)
            {
                ListRemoteChildren(item);
            }

            item.IsExpanded = true;

            foreach (EntryItemViewModel child in item.Children.Where(x => x.IsDirectory))
            {
                RestoreRemoteExpansion(child, expandedPaths, depth + 1);
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"Could not restore expansion for {item.FullPath}: {ex.Message}");
        }
    }

    private async Task ScrollToLastRemoteSelectionAsync()
    {
        string lastPath = _settings.LastRemoteSelectedPath;

        if (string.IsNullOrWhiteSpace(lastPath))
        {
            return;
        }

        EntryItemViewModel? target = FindEntry(RemoteRoots, lastPath);

        if (target == null)
        {
            return;
        }

        await Application.Current.Dispatcher.BeginInvoke(
            () => ScrollToEntryRequested?.Invoke(target),
            DispatcherPriority.Loaded);
    }

    private static EntryItemViewModel? FindEntry(
        IEnumerable<EntryItemViewModel> roots,
        string fullPath)
    {
        foreach (EntryItemViewModel root in roots)
        {
            EntryItemViewModel? result = FindEntryRecursive(root, fullPath);

            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static EntryItemViewModel? FindEntryRecursive(
        EntryItemViewModel item,
        string fullPath)
    {
        if (item.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        foreach (EntryItemViewModel child in item.Children)
        {
            EntryItemViewModel? result = FindEntryRecursive(child, fullPath);

            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    public void OnEntryExpanded(EntryItemViewModel item)
    {
        if (!item.IsDirectory)
        {
            return;
        }

        if (item.Entry is RemoteEntry)
        {
            if (!item.ChildrenLoaded)
            {
                ListRemoteChildren(item);
            }

            List<string> paths = GetRemoteExpandedPaths();

            if (!paths.Contains(item.FullPath))
            {
                paths.Add(item.FullPath);
                SaveRemoteExpandedPaths(paths);
            }
        }
        else if (item.Entry is LocalEntry)
        {
            LastLocalFolder = item;
            _settings.LastLocalFolderPath = item.FullPath;

            if (!_settings.ExpandedLocalPaths.Contains(item.FullPath))
            {
                _settings.ExpandedLocalPaths.Add(item.FullPath);
            }

            SaveSettings();
            EnsureLocalWatcher(item);

            if (!item.ChildrenLoaded)
            {
                ListLocalChildren(item);
            }
        }
    }

    public void OnEntryCollapsed(EntryItemViewModel item)
    {
        if (item.Entry is RemoteEntry)
        {
            List<string> paths = GetRemoteExpandedPaths();
            paths.RemoveAll(x => x.StartsWith(item.FullPath, StringComparison.OrdinalIgnoreCase));
            SaveRemoteExpandedPaths(paths);
        }
        else if (item.Entry is LocalEntry)
        {
            _settings.ExpandedLocalPaths.RemoveAll(
                x => x.StartsWith(item.FullPath, StringComparison.OrdinalIgnoreCase));

            SaveSettings();
            RemoveLocalWatcher(item.FullPath);
        }
    }

    public void OnSelectionChanged(EntryItemViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        if (item.Entry is RemoteEntry)
        {
            _settings.LastRemoteSelectedPath = item.FullPath;
            SaveSettings();
        }
        else if (item.Entry is LocalEntry)
        {
            if (item.IsDirectory)
            {
                LastLocalFolder = item;
            }

            _settings.LastLocalFolderPath = item.FullPath;
            SaveSettings();
        }
    }

    private void ListRemoteChildren(EntryItemViewModel item)
    {
        List<RemoteEntry> children = _sftp.ListDirectory(item.FullPath);

        item.Children.Clear();

        foreach (RemoteEntry child in children)
        {
            item.Children.Add(new EntryItemViewModel(child, item));
        }

        item.ChildrenLoaded = true;
    }

    private void RefreshExpandedRemoteDirectories()
    {
        if (!_sftp.IsConnected)
        {
            return;
        }

        foreach (EntryItemViewModel item in Flatten(RemoteRoots)
            .Where(x => x.IsDirectory && x.ChildrenLoaded && x.IsExpanded)
            .ToList())
        {
            try
            {
                List<RemoteEntry> children = _sftp.ListDirectory(item.FullPath);

                bool changed =
                    children.Count != item.Children.Count ||
                    !children.Select(x => (x.FullPath, x.IsDirectory, x.Size))
                        .SequenceEqual(item.Children.Select(x =>
                            (x.FullPath, x.IsDirectory, x.Size)));

                if (!changed)
                {
                    continue;
                }

                HashSet<string> expandedChildren = item.Children
                    .Where(x => x.IsDirectory && x.IsExpanded)
                    .Select(x => x.FullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                item.Children.Clear();

                foreach (RemoteEntry child in children)
                {
                    EntryItemViewModel childItem = new(child, item)
                    {
                        IsExpanded = expandedChildren.Contains(child.FullPath)
                    };

                    item.Children.Add(childItem);
                }

                item.ChildrenLoaded = true;
            }
            catch (Exception ex)
            {
                LogService.Warn($"Could not refresh remote directory {item.FullPath}: {ex.Message}");
            }
        }
    }

    private void LoadLocalDrives()
    {
        LocalRoots.Clear();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            LocalEntry entry = new()
            {
                Name = drive.Name,
                FullPath = drive.RootDirectory.FullName,
                IsDirectory = true,
                IsDrive = true
            };

            LocalRoots.Add(new EntryItemViewModel(entry));
        }

        RestoreLocalExpansion();
    }

    private void RestoreLocalExpansion()
    {
        HashSet<string> expanded = _settings.ExpandedLocalPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (EntryItemViewModel root in LocalRoots)
        {
            RestoreLocalExpansion(root, expanded, 0);
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastLocalFolderPath))
        {
            LastLocalFolder = FindEntry(LocalRoots, _settings.LastLocalFolderPath);
        }
    }

    private void RestoreLocalExpansion(EntryItemViewModel item, HashSet<string> expanded, int depth)
    {
        if (depth > 10 || !expanded.Contains(item.FullPath))
        {
            return;
        }

        try
        {
            ListLocalChildren(item);
            item.IsExpanded = true;
            EnsureLocalWatcher(item);

            if (item.FullPath.Equals(_settings.LastLocalFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                LastLocalFolder = item;
            }

            foreach (EntryItemViewModel child in item.Children.Where(x => x.IsDirectory))
            {
                RestoreLocalExpansion(child, expanded, depth + 1);
            }
        }
        catch
        {
        }
    }

    private void ListLocalChildren(EntryItemViewModel item)
    {
        item.Children.Clear();

        foreach (string directory in Directory.GetDirectories(item.FullPath))
        {
            item.Children.Add(new EntryItemViewModel(
                new LocalEntry
                {
                    Name = Path.GetFileName(directory),
                    FullPath = directory,
                    IsDirectory = true
                },
                item));
        }

        foreach (string file in Directory.GetFiles(item.FullPath))
        {
            FileInfo info = new(file);

            item.Children.Add(new EntryItemViewModel(
                new LocalEntry
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    Size = info.Length,
                    Created = info.CreationTime,
                    Modified = info.LastWriteTime
                },
                item));
        }

        item.ChildrenLoaded = true;
    }

    private void EnsureLocalWatcher(EntryItemViewModel item)
    {
        if (!item.IsDirectory || _localWatchers.ContainsKey(item.FullPath))
        {
            return;
        }

        try
        {
            FileSystemWatcher watcher = new(item.FullPath)
            {
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Created += (_, _) => OnLocalFileSystemChanged(item);
            watcher.Deleted += (_, _) => OnLocalFileSystemChanged(item);
            watcher.Renamed += (_, _) => OnLocalFileSystemChanged(item);

            _localWatchers[item.FullPath] = watcher;
        }
        catch (Exception ex)
        {
            LogService.Warn($"Could not watch {item.FullPath}: {ex.Message}");
        }
    }

    private void RemoveLocalWatcher(string path)
    {
        if (!_localWatchers.TryGetValue(path, out FileSystemWatcher? watcher))
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        _localWatchers.Remove(path);
    }

    private void OnLocalFileSystemChanged(EntryItemViewModel item)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!item.ChildrenLoaded || !item.IsExpanded)
            {
                return;
            }

            try
            {
                HashSet<string> expanded = item.Children
                    .Where(x => x.IsDirectory && x.IsExpanded)
                    .Select(x => x.FullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                ListLocalChildren(item);

                foreach (EntryItemViewModel child in item.Children)
                {
                    child.IsExpanded = expanded.Contains(child.FullPath);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Could not refresh {item.FullPath}: {ex.Message}");
            }
        });
    }

    public void OnMouseDown(EntryItemViewModel? item, ModifierKeys modifiers)
    {
        if (item == null)
        {
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            item.IsHighlighted = !item.IsHighlighted;
            _selectionAnchor = item;
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            SelectRange(item);
            return;
        }

        bool wasHighlighted = item.IsHighlighted;
        ObservableCollection<EntryItemViewModel> roots = GetRootsFor(item);

        ClearHighlight(roots);

        if (!wasHighlighted || GetHighlightedItems(roots).Count == 0)
        {
            item.IsHighlighted = true;
        }

        _selectionAnchor = item;
    }

    public List<EntryItemViewModel> GetHighlightedItems(
        ObservableCollection<EntryItemViewModel> roots)
    {
        return Flatten(roots).Where(x => x.IsHighlighted).ToList();
    }

    public List<EntryItemViewModel> GetHighlightedLocalItems() =>
        GetHighlightedItems(LocalRoots);

    public List<EntryItemViewModel> GetHighlightedRemoteItems() =>
        GetHighlightedItems(RemoteRoots);

    private void SelectRange(EntryItemViewModel clicked)
    {
        ObservableCollection<EntryItemViewModel> roots = GetRootsFor(clicked);
        List<EntryItemViewModel> visible = Flatten(roots);

        int anchorIndex = _selectionAnchor == null ? -1 : visible.IndexOf(_selectionAnchor);
        int clickedIndex = visible.IndexOf(clicked);

        ClearHighlight(roots);

        if (anchorIndex < 0 || clickedIndex < 0)
        {
            clicked.IsHighlighted = true;
            _selectionAnchor = clicked;
            return;
        }

        for (int i = Math.Min(anchorIndex, clickedIndex);
             i <= Math.Max(anchorIndex, clickedIndex);
             i++)
        {
            visible[i].IsHighlighted = true;
        }
    }

    private ObservableCollection<EntryItemViewModel> GetRootsFor(EntryItemViewModel item)
    {
        EntryItemViewModel current = item;

        while (current.Parent != null)
        {
            current = current.Parent;
        }

        return LocalRoots.Contains(current) ? LocalRoots : RemoteRoots;
    }

    private void ClearHighlight(ObservableCollection<EntryItemViewModel> roots)
    {
        foreach (EntryItemViewModel item in Flatten(roots))
        {
            item.IsHighlighted = false;
        }
    }

    private static List<EntryItemViewModel> Flatten(
        IEnumerable<EntryItemViewModel> roots)
    {
        List<EntryItemViewModel> result = new();

        foreach (EntryItemViewModel root in roots)
        {
            Flatten(root, result);
        }

        return result;
    }

    private static void Flatten(EntryItemViewModel item, List<EntryItemViewModel> output)
    {
        output.Add(item);

        foreach (EntryItemViewModel child in item.Children)
        {
            Flatten(child, output);
        }
    }

    public void HandleRemoteDrop(
        List<EntryItemViewModel> items,
        EntryItemViewModel? target)
    {
        EntryItemViewModel? folder = target?.IsDirectory == true
            ? target
            : target?.Parent ?? LastLocalFolder;

        if (folder == null || !folder.IsDirectory)
        {
            _dialogs.ShowInformation("Download", "Drop the items on a local folder.");
            return;
        }

        foreach (EntryItemViewModel item in items)
        {
            if (item.IsDirectory)
            {
                TransferItemViewModel placeholder = AddScanningPlaceholder(item, folder);
                _ = ScanRemoteDirectoryAsync(item, folder, placeholder);
            }
            else
            {
                StartDownload(item.Entry, Path.Combine(folder.FullPath, item.Name));
            }
        }

        OnTransferCollectionChanged();
    }

    private TransferItemViewModel AddScanningPlaceholder(
        EntryItemViewModel directory,
        EntryItemViewModel target)
    {
        TransferEntry entry = new()
        {
            FileName = directory.Name,
            RemotePath = directory.FullPath,
            LocalPath = target.FullPath,
            Status = "Scanning"
        };

        TransferItemViewModel placeholder = new(entry);
        Transfers.Add(placeholder);

        return placeholder;
    }

    private async Task ScanRemoteDirectoryAsync(
        EntryItemViewModel directory,
        EntryItemViewModel target,
        TransferItemViewModel placeholder)
    {
        try
        {
            List<RemoteEntry> files = await Task.Run(
                () => _sftp.ListFilesRecursive(directory.FullPath));

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Transfers.Remove(placeholder);

                string prefix = directory.FullPath.TrimEnd('/');

                foreach (RemoteEntry file in files)
                {
                    string relative = file.FullPath[prefix.Length..].TrimStart('/');
                    string localPath = Path.Combine(
                        target.FullPath,
                        directory.Name,
                        relative.Replace('/', Path.DirectorySeparatorChar));

                    StartDownload(file, localPath);
                }

                OnTransferCollectionChanged();
            });
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                placeholder.Status = "Error";
                placeholder.ErrorMessage = ex.Message;
                SaveQueue();
            });
        }
    }

    public void HandleLocalDrop(
        List<EntryItemViewModel> items,
        EntryItemViewModel? target)
    {
        EntryItemViewModel? directory = target?.IsDirectory == true
            ? target
            : target?.Parent;

        if (directory == null)
        {
            _dialogs.ShowInformation("Upload", "Drop the items on a remote folder.");
            return;
        }

        foreach (EntryItemViewModel item in items)
        {
            if (item.IsDirectory)
            {
                UploadLocalDirectory(item, directory);
            }
            else
            {
                StartUpload(
                    item.Entry,
                    CombineRemotePath(directory.FullPath, item.Name));
            }
        }

        OnTransferCollectionChanged();
    }

    private void UploadLocalDirectory(
        EntryItemViewModel directory,
        EntryItemViewModel target)
    {
        try
        {
            string remoteDirectory = CombineRemotePath(target.FullPath, directory.Name);

            _sftp.CreateDirectory(remoteDirectory);

            foreach (string file in Directory.GetFiles(directory.FullPath))
            {
                StartUpload(
                    new LocalEntry
                    {
                        Name = Path.GetFileName(file),
                        FullPath = file,
                        Size = new FileInfo(file).Length
                    },
                    CombineRemotePath(remoteDirectory, Path.GetFileName(file)));
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Upload directory failed: {ex}");
            _dialogs.ShowError("Upload error", ex.Message);
        }
    }

    private static string CombineRemotePath(string directory, string name)
    {
        return directory.EndsWith("/", StringComparison.Ordinal)
            ? directory + name
            : directory + "/" + name;
    }

    private void StartDownload(Entry remoteEntry, string localPath)
    {
        if (Transfers.Any(x =>
                x.RemotePath.Equals(remoteEntry.FullPath, StringComparison.OrdinalIgnoreCase) &&
                x.LocalPath.Equals(localPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        TransferEntry entry = new()
        {
            FileName = Path.GetFileName(localPath),
            RemotePath = remoteEntry.FullPath,
            LocalPath = localPath,
            TotalLength = remoteEntry.Size,
            Direction = TransferDirection.Download
        };

        QueueTransfer(new TransferItemViewModel(entry));
    }

    private void StartUpload(Entry localEntry, string remotePath)
    {
        TransferEntry entry = new()
        {
            FileName = localEntry.Name,
            RemotePath = remotePath,
            LocalPath = localEntry.FullPath,
            TotalLength = localEntry.Size,
            Direction = TransferDirection.Upload
        };

        QueueTransfer(new TransferItemViewModel(entry));
    }

    private void QueueTransfer(TransferItemViewModel item)
    {
        Transfers.Add(item);
        item.Status = "Queued";

        if (!_pendingTransfers.Contains(item))
        {
            _pendingTransfers.Add(item);
        }

        StartNextQueuedTransfer();
        OnTransferCollectionChanged();
    }

    private void StartNextQueuedTransfer()
    {
        int max = Math.Clamp(_settings.MaxConcurrentTransfers, 1, 8);

        while (_activeTransferIds.Count < max && _pendingTransfers.Count > 0)
        {
            TransferItemViewModel next = _pendingTransfers[0];
            _pendingTransfers.RemoveAt(0);

            if (next.IsFinished || !Transfers.Contains(next))
            {
                continue;
            }

            CancellationTokenSource cts = new();
            _cancellationTokens[next.Entry.Id] = cts;
            _activeTransferIds.Add(next.Entry.Id);

            next.Status = "Active";
            _ = RunTransferAsync(next, cts.Token);
        }
    }

    private async Task RunTransferAsync(
        TransferItemViewModel item,
        CancellationToken token)
    {
        try
        {
            if (item.Entry.Direction == TransferDirection.Upload)
            {
                await RunUploadAsync(item, token);
            }
            else
            {
                await RunDownloadAsync(item, token);
            }

            item.SyncStatus();
            item.RefreshProgress();
        }
        catch (OperationCanceledException)
        {
            item.Status = "Paused";
            item.TransferSpeed = 0;
        }
        catch (Exception ex)
        {
            item.Status = "Error";
            item.ErrorMessage = ex.Message;
            item.TransferSpeed = 0;
        }
        finally
        {
            _activeTransferIds.Remove(item.Entry.Id);
            _cancellationTokens.Remove(item.Entry.Id);

            OnTransferCollectionChanged();
            SaveQueue();
            StartNextQueuedTransfer();
        }
    }

    private async Task RunDownloadAsync(
        TransferItemViewModel item,
        CancellationToken token)
    {
        RemoteEntry remoteFile = new()
        {
            Name = Path.GetFileName(item.RemotePath),
            FullPath = item.RemotePath,
            Size = item.TotalLength
        };

        await _transferService.DownloadAsync(
            _settings,
            remoteFile,
            item.LocalPath,
            _settings.ParallelConnections,
            item.Entry,
            token);
    }

    private async Task RunUploadAsync(
        TransferItemViewModel item,
        CancellationToken token)
    {
        long lastReported = 0;
        DateTime lastUpdate = DateTime.UtcNow;

        await _sftp.UploadFileAsync(
            item.LocalPath,
            item.RemotePath,
            progress => UpdateTransferProgress(
                item,
                progress,
                ref lastReported,
                ref lastUpdate),
            token,
            startOffset: 0);
    }

    private void UpdateTransferProgress(
        TransferItemViewModel item,
        long progress,
        ref long lastReported,
        ref DateTime lastUpdate)
    {
        long delta = progress - lastReported;
        DateTime now = DateTime.UtcNow;

        if (now - lastUpdate < TimeSpan.FromMilliseconds(200) &&
            progress < item.TotalLength)
        {
            return;
        }

        item.CompletedLength = progress;

        double seconds = (now - lastUpdate).TotalSeconds;

        if (seconds > 0)
        {
            item.TransferSpeed = (long)(delta / seconds);
        }

        lastReported = progress;
        lastUpdate = now;
        item.RefreshProgress();
    }

    private void LoadPersistedTransfers()
    {
        List<TransferEntry> persisted = TransferQueueService.Load(_currentConnectionKey);
        int restored = 0;

        foreach (TransferEntry entry in persisted)
        {
            bool exists = Transfers.Any(x =>
                x.RemotePath.Equals(entry.RemotePath, StringComparison.OrdinalIgnoreCase) &&
                x.LocalPath.Equals(entry.LocalPath, StringComparison.OrdinalIgnoreCase));

            if (exists)
            {
                continue;
            }

            if (!entry.IsFinished)
            {
                entry.Status = "Paused";
            }

            Transfers.Add(new TransferItemViewModel(entry));
            restored++;
        }

        if (restored > 0)
        {
            LogService.Info($"Restored {restored} unfinished transfers in a paused state.");
        }
    }

    private void SaveQueue()
    {
        if (string.IsNullOrWhiteSpace(_currentConnectionKey))
        {
            return;
        }

        List<TransferEntry> snapshot = Transfers.Select(x => x.Entry).ToList();
        string connectionKey = _currentConnectionKey;

        Task.Run(async () =>
        {
            await _queueSaveGate.WaitAsync();

            try
            {
                TransferQueueService.Save(connectionKey, snapshot);
            }
            finally
            {
                _queueSaveGate.Release();
            }
        });
    }

    private void PollTransfers(object? sender, EventArgs e)
    {
        foreach (TransferItemViewModel item in Transfers.ToList())
        {
            long completed = item.CompletedLength;
            long delta = completed - item.LastPollCompleted;

            item.TransferSpeed = (long)(delta / _pollTimer.Interval.TotalSeconds);
            item.LastPollCompleted = completed;
            item.RefreshProgress();
        }

        OnTransferCollectionChanged();

        // Persist frequently so a killed process can resume from the last saved state.
        SaveQueue();
    }

    public void PauseTransfer(TransferItemViewModel item)
    {
        if (item.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase))
        {
            _pendingTransfers.Remove(item);
            item.Status = "Paused";
        }
        else if (_cancellationTokens.TryGetValue(item.Entry.Id, out CancellationTokenSource? cts))
        {
            cts.Cancel();
            item.Status = "Paused";
            item.TransferSpeed = 0;
        }

        OnTransferCollectionChanged();
        SaveQueue();
    }

    public void ResumeTransfer(TransferItemViewModel item)
    {
        if (item.IsFinished)
        {
            return;
        }

        if (!_sftp.IsConnected)
        {
            _dialogs.ShowInformation(
                "Resume",
                "Connect to the SFTP server first, then resume the transfer.");

            return;
        }

        item.Status = "Queued";

        if (!_pendingTransfers.Contains(item))
        {
            _pendingTransfers.Add(item);
        }

        StartNextQueuedTransfer();
    }

    public void RemoveTransfer(TransferItemViewModel item)
    {
        _pendingTransfers.Remove(item);

        if (_cancellationTokens.TryGetValue(item.Entry.Id, out CancellationTokenSource? cts))
        {
            cts.Cancel();
            _cancellationTokens.Remove(item.Entry.Id);
        }

        string sidecarPath = ParallelSftpTransferService.GetSidecarPath(item.LocalPath);

        Task.Delay(300).ContinueWith(_ =>
        {
            try
            {
                if (File.Exists(sidecarPath))
                {
                    File.Delete(sidecarPath);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Could not delete sidecar for {item.RemotePath}: {ex.Message}");
            }
        });

        Transfers.Remove(item);
        _activeTransferIds.Remove(item.Entry.Id);

        OnTransferCollectionChanged();
        SaveQueue();
        StartNextQueuedTransfer();
    }

    public void ClearQueue()
    {
        List<TransferItemViewModel> items = Transfers.ToList();

        foreach (TransferItemViewModel item in items)
        {
            _pendingTransfers.Remove(item);

            if (item.Status.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
                _cancellationTokens.TryGetValue(item.Entry.Id, out CancellationTokenSource? cts))
            {
                cts.Cancel();
            }

            item.Status = "Paused";
        }

        Transfers.Clear();

        foreach (TransferItemViewModel item in items)
        {
            string sidecarPath = ParallelSftpTransferService.GetSidecarPath(item.LocalPath);

            Task.Delay(500).ContinueWith(_ =>
            {
                try
                {
                    if (File.Exists(sidecarPath))
                    {
                        File.Delete(sidecarPath);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warn($"Could not delete sidecar: {ex.Message}");
                }
            });
        }

        OnTransferCollectionChanged();
        SaveQueue();
    }

    private void RemoveSelected(IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0)
        {
            return;
        }

        foreach (TransferItemViewModel item in selectedItems
            .OfType<TransferItemViewModel>()
            .ToList())
        {
            RemoveTransfer(item);
        }
    }

    private void PauseAll()
    {
        foreach (TransferItemViewModel item in Transfers.Where(x => x.CanPause).ToList())
        {
            PauseTransfer(item);
        }
    }

    private void ResumeAll()
    {
        foreach (TransferItemViewModel item in Transfers.Where(x => x.CanResume).ToList())
        {
            ResumeTransfer(item);
        }
    }

    public void NewLocalFolder(EntryItemViewModel item)
    {
        EntryItemViewModel folder = item.IsDirectory ? item : item.Parent ?? item;

        if (!folder.IsDirectory)
        {
            return;
        }

        string? name = _dialogs.PromptText("New folder", "Folder name", "New folder");

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            string path = Path.Combine(folder.FullPath, name);
            Directory.CreateDirectory(path);

            if (!folder.ChildrenLoaded)
            {
                ListLocalChildren(folder);
            }
            else
            {
                folder.Children.Insert(
                    0,
                    new EntryItemViewModel(
                        new LocalEntry
                        {
                            Name = name,
                            FullPath = path,
                            IsDirectory = true
                        },
                        folder));
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Create folder failed: {ex}");
            _dialogs.ShowError("Create folder error", ex.Message);
        }
    }

    public void DeleteEntries(IList? items, bool isRemote)
    {
        List<EntryItemViewModel> entries = items?
            .OfType<EntryItemViewModel>()
            .ToList() ?? new List<EntryItemViewModel>();

        if (entries.Count == 0)
        {
            return;
        }

        if (isRemote && entries.Any(x => x.Entry is not RemoteEntry))
        {
            return;
        }

        if (!isRemote && entries.Any(x => x.Entry is not LocalEntry))
        {
            return;
        }

        string message = entries.Count == 1
            ? $"Delete '{entries[0].Name}'?"
            : $"Delete {entries.Count} items?";

        if (!_dialogs.Confirm("Confirm delete", message))
        {
            return;
        }

        foreach (EntryItemViewModel item in entries.ToList())
        {
            try
            {
                if (isRemote)
                {
                    _sftp.DeleteDirectory(item.FullPath);
                }
                else if (item.IsDrive)
                {
                    continue;
                }
                else if (item.IsDirectory)
                {
                    Directory.Delete(item.FullPath, true);
                }
                else
                {
                    File.Delete(item.FullPath);
                }

                item.Parent?.Children.Remove(item);
            }
            catch (Exception ex)
            {
                LogService.Error($"Delete failed: {ex}");
                _dialogs.ShowError("Delete error", ex.Message);
            }
        }
    }

    private void UploadToRemote(EntryItemViewModel target)
    {
        EntryItemViewModel? directory = target.IsDirectory ? target : target.Parent;

        if (directory == null)
        {
            return;
        }

        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = $"Upload files to {directory.Name}",
            Multiselect = true,
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (string localPath in dialog.FileNames)
        {
            FileInfo file = new(localPath);

            StartUpload(
                new LocalEntry
                {
                    Name = file.Name,
                    FullPath = localPath,
                    Size = file.Length
                },
                CombineRemotePath(directory.FullPath, file.Name));
        }

        OnTransferCollectionChanged();
    }

    private void NewRemoteFolder(EntryItemViewModel target)
    {
        EntryItemViewModel? parent = target.IsDirectory ? target : target.Parent ?? target;

        string? name = _dialogs.PromptText("New remote folder", "Folder name", "New folder");

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string remotePath = CombineRemotePath(parent.FullPath, name);

        try
        {
            _sftp.CreateDirectory(remotePath);

            if (parent.ChildrenLoaded)
            {
                ListRemoteChildren(parent);
            }
            else
            {
                parent.IsExpanded = true;
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Create remote folder failed: {ex}");
            _dialogs.ShowError("Create folder error", ex.Message);
        }
    }

    private void SaveRemoteExpandedPaths(List<string> paths)
    {
        _settings.ExpandedRemotePathsByConnection[_currentConnectionKey] = paths;
        SaveSettings();
    }

    private void SaveSettings() =>
        EncryptedSettingsService.Save(_settings);

    private void OnTransferCollectionChanged()
    {
        OnPropertyChanged(nameof(QueueCountDisplay));
        OnPropertyChanged(nameof(TotalSizeDisplay));
        OnPropertyChanged(nameof(TotalSpeedDisplay));
    }

    public void SaveStateOnClose()
    {
        _pollTimer.Stop();
        _remoteRefreshTimer.Stop();

        Disconnect();

        foreach (FileSystemWatcher watcher in _localWatchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _localWatchers.Clear();
        SaveSettings();

        LogService.Info("StreamZilla closed.");
    }
}

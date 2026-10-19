using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
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
    private readonly SftpService _sftp = new SftpService();
    private readonly ParallelSftpDownloadService _downloader = new ParallelSftpDownloadService();
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, CancellationTokenSource> _cancellationTokens = new Dictionary<string, CancellationTokenSource>();
    private readonly DispatcherTimer _pollTimer = new DispatcherTimer();
    private readonly DispatcherTimer _remoteRefreshTimer = new DispatcherTimer();
    private readonly Dictionary<string, FileSystemWatcher> _localWatchers = new Dictionary<string, FileSystemWatcher>();
    private readonly List<TransferItemViewModel> _pendingQueue = new List<TransferItemViewModel>();
    private readonly HashSet<string> _activeDownloadIds = new HashSet<string>();
    private readonly SemaphoreSlim _queueSaveGate = new SemaphoreSlim(1, 1);

    private AppSettings _settings;
    private string _currentConnectionKey = "";
    private RemoteFileViewModel? _selectionAnchor;
    private LocalFileViewModel? _lastLocalFolder;
    private string _maxStreamsDisplay;
    private string _maxFilesDisplay;

    public MainViewModel(IDialogService dialogService)
    {
        _dialogs = dialogService;
        _settings = EncryptedSettingsService.Load();

        _maxStreamsDisplay = Math.Clamp(_settings.ParallelConnections, 1, 8).ToString();
        _maxFilesDisplay = Math.Clamp(_settings.MaxConcurrentDownloads, 1, 8).ToString();

        Downloads = new ObservableCollection<TransferItemViewModel>();
        LocalRoots = new ObservableCollection<LocalFileViewModel>();
        RemoteRoots = new ObservableCollection<RemoteFileViewModel>();

        Downloads.CollectionChanged += (object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs _) =>
        {
            OnPropertyChanged(nameof(QueueCountDisplay));
            OnPropertyChanged(nameof(TotalSizeDisplay));
        };

        ConnectCommand = new RelayCommand(async (object? _) => await ConnectAsync(), (object? _) => !IsBusy);
        DisconnectCommand = new RelayCommand((object? _) => Disconnect());
        RefreshRemoteCommand = new RelayCommand(async (object? _) => await RefreshRemoteAsync(), (object? _) => _sftp.IsConnected);
        PauseAllCommand = new RelayCommand((object? _) => PauseAll(), (object? _) => Downloads.Any(x => x.CanPause));
        ResumeAllCommand = new RelayCommand((object? _) => ResumeAll(), (object? _) => Downloads.Any(x => x.CanResume));
        ClearQueueCommand = new RelayCommand((object? _) => ClearQueue(), (object? _) => Downloads.Count > 0);
        RemoveSelectedCommand = new RelayCommand(
            (object? parameter) => RemoveSelected(parameter as IList),
            (object? parameter) => parameter is IList list && list.Count > 0);
        PauseDownloadCommand = new RelayCommand(
            (object? parameter) => { if (parameter is TransferItemViewModel download) { PauseDownload(download); } },
            (object? parameter) => parameter is TransferItemViewModel download && download.CanPause);
        ResumeDownloadCommand = new RelayCommand(
            (object? parameter) => { if (parameter is TransferItemViewModel download) { ResumeDownload(download); } },
            (object? parameter) => parameter is TransferItemViewModel download && download.CanResume);
        RemoveDownloadCommand = new RelayCommand((object? parameter) => { if (parameter is TransferItemViewModel download) { RemoveDownload(download); } });
        NewFolderCommand = new RelayCommand((object? parameter) => { if (parameter is LocalFileViewModel localItem) { NewFolder(localItem); } });
        DeleteLocalItemCommand = new RelayCommand((object? parameter) => { if (parameter is LocalFileViewModel localItem) { DeleteLocalItem(localItem); } });
        UploadCommand = new RelayCommand(
            (object? parameter) => { if (parameter is RemoteFileViewModel remoteItem) { UploadToRemote(remoteItem); } },
            (object? parameter) => _sftp.IsConnected);
        NewRemoteFolderCommand = new RelayCommand(
            (object? parameter) => { if (parameter is RemoteFileViewModel remoteItem) { NewRemoteFolder(remoteItem); } },
            (object? parameter) => _sftp.IsConnected);
        DeleteRemoteItemCommand = new RelayCommand(
            (object? parameter) => { if (parameter is RemoteFileViewModel remoteItem) { DeleteRemoteItem(remoteItem); } },
            (object? parameter) => _sftp.IsConnected);

        LoadLocalDrives();

        _pollTimer.Interval = TimeSpan.FromMilliseconds(500);
        _pollTimer.Tick += PollDownloads;
        _pollTimer.Start();

        _remoteRefreshTimer.Interval = TimeSpan.FromSeconds(3);
        _remoteRefreshTimer.Tick += (object? _, EventArgs _) => RefreshExpandedRemoteDirectories();
        _remoteRefreshTimer.Start();

        LogService.Info("StreamZilla started.");
    }

    public event Action<RemoteFileViewModel>? ScrollToRemoteItemRequested;

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand RefreshRemoteCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ResumeAllCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand PauseDownloadCommand { get; }
    public RelayCommand ResumeDownloadCommand { get; }
    public RelayCommand RemoveDownloadCommand { get; }
    public RelayCommand NewFolderCommand { get; }
    public RelayCommand DeleteLocalItemCommand { get; }
    public RelayCommand UploadCommand { get; }
    public RelayCommand NewRemoteFolderCommand { get; }
    public RelayCommand DeleteRemoteItemCommand { get; }

    public ObservableCollection<TransferItemViewModel> Downloads { get; }
    public ObservableCollection<LocalFileViewModel> LocalRoots { get; }
    public ObservableCollection<RemoteFileViewModel> RemoteRoots { get; }

    public bool IsConnected
    {
        get
        {
            return _sftp.IsConnected;
        }
    }

    public bool IsBusy { get; private set; }

    public string Host
    {
        get
        {
            return _settings.Host;
        }
        set
        {
            _settings.Host = value;
            OnPropertyChanged();
        }
    }

    public string Port
    {
        get
        {
            return _settings.Port.ToString();
        }
        set
        {
            if (int.TryParse(value, out int port))
            {
                _settings.Port = port;
            }

            OnPropertyChanged();
        }
    }

    public string Username
    {
        get
        {
            return _settings.Username;
        }
        set
        {
            _settings.Username = value;
            OnPropertyChanged();
        }
    }

    public string Password
    {
        get
        {
            return _settings.Password;
        }
        set
        {
            _settings.Password = value;
            OnPropertyChanged();
        }
    }

    public string MaxStreamsPerFile
    {
        get
        {
            return _maxStreamsDisplay;
        }
        set
        {
            if (int.TryParse(value, out int streams) && streams >= 1 && streams <= 8)
            {
                _maxStreamsDisplay = value;
                _settings.ParallelConnections = streams;
            }
            else
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _dialogs.ShowError(
                        "Invalid value",
                        "Max streams per file must be a whole number between 1 and 8.");
                });
            }

            OnPropertyChanged();
        }
    }

    public string MaxConcurrentFiles
    {
        get
        {
            return _maxFilesDisplay;
        }
        set
        {
            if (int.TryParse(value, out int max) && max >= 1 && max <= 8)
            {
                _maxFilesDisplay = value;
                _settings.MaxConcurrentDownloads = max;
            }
            else
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    _dialogs.ShowError(
                        "Invalid value",
                        "Max concurrent files must be a whole number between 1 and 8.");
                });
            }

            OnPropertyChanged();
        }
    }

    public TransferItemViewModel? SelectedDownload { get; set; }

    public LocalFileViewModel? LastLocalFolder
    {
        get
        {
            return _lastLocalFolder;
        }
        private set
        {
            _lastLocalFolder = value;
        }
    }

    public string QueueCountDisplay
    {
        get
        {
            int downloaded = Downloads.Count(x => x.IsDownloaded);
            int total = Downloads.Count;

            return $"{downloaded} / {total}";
        }
    }

    public string TotalSizeDisplay
    {
        get
        {
            long completed = Downloads.Sum(x => x.CompletedLength);
            long total = Downloads.Sum(x => x.TotalLength);

            return $"{FileIconHelper.FormatSize(completed)} / {FileIconHelper.FormatSize(total)}";
        }
    }

    public string TotalSpeedDisplay
    {
        get
        {
            long total = Downloads.Sum(x => x.DownloadSpeed);

            if (total <= 0)
            {
                return "";
            }

            if (total >= 1_048_576)
            {
                return $"{total / 1_048_576.0:F1} MB/s";
            }

            return $"{total / 1024.0:F0} KB/s";
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
                _settings.Host, _settings.Port, _settings.Username);

            RemoteRoots.Clear();
            await LoadRemoteRootAsync();

            LoadPersistedDownloads();

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

        string hostKey = $"{_settings.Host.Trim().ToLowerInvariant()}|{_settings.Port}";

        if (_settings.KnownHostKeys.TryGetValue(hostKey, out string? storedFingerprint))
        {
            if (storedFingerprint.Equals(fingerprint, StringComparison.Ordinal))
            {
                return true;
            }

            _dialogs.ShowError(
                "SSH host key changed",
                $"The SSH host key for {_settings.Host}:{_settings.Port} has changed.\n\n" +
                $"Stored fingerprint:\n{storedFingerprint}\n\n" +
                $"Current fingerprint:\n{fingerprint}\n\n" +
                "Connection refused. If this change is expected, remove the saved host key for this connection.");

            LogService.Warn($"SSH host key mismatch for {hostKey}.");
            return false;
        }

        bool accepted = _dialogs.ConfirmHostKey(_settings.Host, _settings.Port, fingerprint);

        if (!accepted)
        {
            LogService.Info($"User rejected SSH host key for {hostKey}.");
            return false;
        }

        _settings.KnownHostKeys[hostKey] = fingerprint;
        SaveSettings();

        LogService.Info($"Accepted and saved SSH host key for {hostKey}.");
        return true;
    }

    public void Disconnect()
    {
        try
        {
            foreach (TransferItemViewModel item in Downloads.ToList())
            {
                if ((item.Status.Equals("Active", StringComparison.OrdinalIgnoreCase) ||
                     item.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase)) &&
                    _cancellationTokens.TryGetValue(item.Entry.Id, out CancellationTokenSource? cts))
                {
                    cts.Cancel();
                }
            }

            SaveQueue();
            _sftp.Disconnect();
            RemoteRoots.Clear();
            ClearHighlight();
            _selectionAnchor = null;

            OnPropertyChanged(nameof(IsConnected));
            LogService.Info("Disconnected by user. Unfinished downloads were saved for this connection.");
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
        RemoteFileEntry rootEntry = new RemoteFileEntry
        {
            Name = "/",
            FullPath = "/",
            IsDirectory = true
        };

        RemoteFileViewModel root = new RemoteFileViewModel(rootEntry)
        {
            IsExpanded = true,
            ChildrenLoaded = true
        };

        foreach (RemoteFileEntry child in _sftp.ListDirectory("/"))
        {
            root.Children.Add(new RemoteFileViewModel(child, root));
        }

        RemoteRoots.Add(root);

        List<string> expandedPaths = GetRemoteExpandedPaths();
        RestoreRemoteExpansionRecursive(root, expandedPaths, 0);

        await ScrollToLastRemoteSelectionAsync();
    }

    private List<string> GetRemoteExpandedPaths()
    {
        if (_settings.ExpandedRemotePathsByConnection.TryGetValue(_currentConnectionKey, out List<string>? paths))
        {
            return paths;
        }

        return new List<string>();
    }

    private void RestoreRemoteExpansionRecursive(RemoteFileViewModel item, List<string> expandedPaths, int depth)
    {
        if (depth > 20 || !expandedPaths.Contains(item.FullPath))
        {
            return;
        }

        try
        {
            if (!item.ChildrenLoaded)
            {
                List<RemoteFileEntry> children = _sftp.ListDirectory(item.FullPath);
                item.Children.Clear();

                foreach (RemoteFileEntry child in children)
                {
                    item.Children.Add(new RemoteFileViewModel(child, item));
                }

                item.ChildrenLoaded = true;
            }

            item.IsExpanded = true;

            foreach (RemoteFileViewModel child in item.Children.Where(x => x.IsDirectory))
            {
                RestoreRemoteExpansionRecursive(child, expandedPaths, depth + 1);
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

        RemoteFileViewModel? target = FindRemoteItemByPath(lastPath);

        if (target == null)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            ScrollToRemoteItemRequested?.Invoke(target);
        }, DispatcherPriority.Loaded);
    }

    private RemoteFileViewModel? FindRemoteItemByPath(string path)
    {
        foreach (RemoteFileViewModel root in RemoteRoots)
        {
            RemoteFileViewModel? found = FindRemoteItemRecursive(root, path);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private RemoteFileViewModel? FindRemoteItemRecursive(RemoteFileViewModel item, string path)
    {
        if (item.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        foreach (RemoteFileViewModel child in item.Children)
        {
            RemoteFileViewModel? found = FindRemoteItemRecursive(child, path);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    public void OnRemoteItemExpanded(RemoteFileViewModel item)
    {
        if (!item.IsDirectory)
        {
            return;
        }

        if (!item.ChildrenLoaded)
        {
            try
            {
                List<RemoteFileEntry> children = _sftp.ListDirectory(item.FullPath);
                item.Children.Clear();

                foreach (RemoteFileEntry child in children)
                {
                    item.Children.Add(new RemoteFileViewModel(child, item));
                }

                item.ChildrenLoaded = true;
            }
            catch (Exception ex)
            {
                LogService.Error($"Failed to list {item.FullPath}: {ex}");
                _dialogs.ShowError("Error", ex.Message);
                return;
            }
        }

        List<string> expandedPaths = GetRemoteExpandedPaths();

        if (!expandedPaths.Contains(item.FullPath))
        {
            expandedPaths.Add(item.FullPath);
            SaveRemoteExpandedPaths(expandedPaths);
        }
    }

    public void OnRemoteItemCollapsed(RemoteFileViewModel item)
    {
        List<string> expandedPaths = GetRemoteExpandedPaths();
        expandedPaths.RemoveAll(x => x.StartsWith(item.FullPath, StringComparison.OrdinalIgnoreCase));
        SaveRemoteExpandedPaths(expandedPaths);
    }

    private void SaveRemoteExpandedPaths(List<string> expandedPaths)
    {
        _settings.ExpandedRemotePathsByConnection[_currentConnectionKey] = expandedPaths;
        SaveSettings();
    }

    public void OnRemoteSelectionChanged(RemoteFileViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        _settings.LastRemoteSelectedPath = item.FullPath;
        SaveSettings();
    }

    private bool _isRefreshingRemoteDirectories;

    private void RefreshExpandedRemoteDirectories()
    {
        if (!_sftp.IsConnected || _isRefreshingRemoteDirectories)
        {
            return;
        }

        List<RemoteFileViewModel> expandedDirectories = FlattenVisible()
            .Where(x => x.IsDirectory && x.ChildrenLoaded && x.IsExpanded)
            .ToList();

        if (expandedDirectories.Count == 0)
        {
            return;
        }

        _isRefreshingRemoteDirectories = true;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (RemoteFileViewModel item in expandedDirectories)
                {
                    List<RemoteFileEntry> children;

                    try
                    {
                        children = _sftp.ListDirectory(item.FullPath);
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn($"Could not refresh remote directory {item.FullPath}: {ex.Message}");
                        continue;
                    }

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        bool changed = children.Count != item.Children.Count ||
                            !children.Select(c => (c.FullPath, c.IsDirectory, c.Size))
                                .SequenceEqual(item.Children.Select(c => (c.FullPath, c.IsDirectory, c.Size)));

                        if (!changed)
                        {
                            return;
                        }

                        HashSet<string> expandedChildPaths = item.Children
                            .Where(c => c.IsDirectory && c.IsExpanded)
                            .Select(c => c.FullPath)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        item.Children.Clear();

                        foreach (RemoteFileEntry child in children)
                        {
                            item.Children.Add(new RemoteFileViewModel(child, item));
                        }

                        foreach (RemoteFileViewModel child in item.Children)
                        {
                            if (expandedChildPaths.Contains(child.FullPath))
                            {
                                child.IsExpanded = true;
                            }
                        }
                    });
                }
            }
            finally
            {
                _isRefreshingRemoteDirectories = false;
            }
        });
    }

    // ---------- Local tree ----------

    private void LoadLocalDrives()
    {
        LocalRoots.Clear();

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            LocalFileEntry entry = new LocalFileEntry
            {
                Name = drive.Name,
                FullPath = drive.RootDirectory.FullName,
                IsDirectory = true,
                IsDrive = true
            };

            LocalRoots.Add(new LocalFileViewModel(entry));
        }

        RestoreLocalExpansion();
    }

    private void RestoreLocalExpansion()
    {
        HashSet<string> expandedPaths = _settings.ExpandedLocalPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (LocalFileViewModel root in LocalRoots)
        {
            RestoreLocalExpansionRecursive(root, expandedPaths, 0);
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastLocalFolderPath))
        {
            LocalFileViewModel? last = FindLocalItemByPath(_settings.LastLocalFolderPath);

            if (last != null && last.IsDirectory)
            {
                LastLocalFolder = last;
            }
        }
    }

    private void RestoreLocalExpansionRecursive(LocalFileViewModel item, HashSet<string> expandedPaths, int depth)
    {
        if (depth > 10 || !expandedPaths.Contains(item.FullPath))
        {
            return;
        }

        try
        {
            LoadLocalChildren(item);
            item.IsExpanded = true;
            EnsureLocalWatcher(item);

            if (item.FullPath.Equals(_settings.LastLocalFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                LastLocalFolder = item;
            }

            foreach (LocalFileViewModel child in item.Children.Where(x => x.IsDirectory))
            {
                RestoreLocalExpansionRecursive(child, expandedPaths, depth + 1);
            }
        }
        catch
        {
        }
    }

    private LocalFileViewModel? FindLocalItemByPath(string path)
    {
        foreach (LocalFileViewModel root in LocalRoots)
        {
            LocalFileViewModel? found = FindLocalItemRecursive(root, path);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private LocalFileViewModel? FindLocalItemRecursive(LocalFileViewModel item, string path)
    {
        if (item.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        foreach (LocalFileViewModel child in item.Children)
        {
            LocalFileViewModel? found = FindLocalItemRecursive(child, path);

            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private void LoadLocalChildren(LocalFileViewModel item)
    {
        item.Children.Clear();

        foreach (string directory in Directory.GetDirectories(item.FullPath))
        {
            LocalFileEntry entry = new LocalFileEntry
            {
                Name = Path.GetFileName(directory),
                FullPath = directory,
                IsDirectory = true
            };

            item.Children.Add(new LocalFileViewModel(entry, item));
        }

        foreach (string file in Directory.GetFiles(item.FullPath))
        {
            FileInfo fileInfo = new FileInfo(file);

            LocalFileEntry entry = new LocalFileEntry
            {
                Name = Path.GetFileName(file),
                FullPath = file,
                IsDirectory = false,
                Size = fileInfo.Length
            };

            item.Children.Add(new LocalFileViewModel(entry, item));
        }

        item.ChildrenLoaded = true;
    }

    public void OnLocalItemExpanded(LocalFileViewModel item)
    {
        if (item.IsDirectory)
        {
            LastLocalFolder = item;
            _settings.LastLocalFolderPath = item.FullPath;

            if (!_settings.ExpandedLocalPaths.Contains(item.FullPath))
            {
                _settings.ExpandedLocalPaths.Add(item.FullPath);
            }

            SaveSettings();
            EnsureLocalWatcher(item);
        }

        if (!item.IsDirectory || item.ChildrenLoaded)
        {
            return;
        }

        try
        {
            LoadLocalChildren(item);
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to load local folder: {ex}");
            _dialogs.ShowError("Error", ex.Message);
        }
    }

    public void OnLocalItemCollapsed(LocalFileViewModel item)
    {
        _settings.ExpandedLocalPaths.RemoveAll(x => x.StartsWith(item.FullPath, StringComparison.OrdinalIgnoreCase));
        RemoveLocalWatcher(item.FullPath);
        SaveSettings();
    }

    public void OnLocalSelectionChanged(LocalFileViewModel? item)
    {
        if (item != null)
        {
            if (item.IsDirectory || item.IsDrive)
            {
                LastLocalFolder = item;
                _settings.LastLocalFolderPath = item.FullPath;
            }
            else if (item.LocalParent != null)
            {
                LastLocalFolder = item.LocalParent;
                _settings.LastLocalFolderPath = item.LocalParent.FullPath;
            }
            SaveSettings();
        }
    }

    private void EnsureLocalWatcher(LocalFileViewModel item)
    {
        if (!item.IsDirectory || _localWatchers.ContainsKey(item.FullPath))
        {
            return;
        }

        try
        {
            FileSystemWatcher watcher = new FileSystemWatcher(item.FullPath)
            {
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Created += (_, e) => OnLocalFileSystemChanged(item);
            watcher.Deleted += (_, e) => OnLocalFileSystemChanged(item);
            watcher.Renamed += (_, e) => OnLocalFileSystemChanged(item);

            _localWatchers[item.FullPath] = watcher;
            LogService.Info($"Watching local directory for changes: {item.FullPath}");
        }
        catch (Exception ex)
        {
            LogService.Warn($"Could not watch local directory {item.FullPath}: {ex.Message}");
        }
    }

    private void RemoveLocalWatcher(string path)
    {
        if (_localWatchers.TryGetValue(path, out FileSystemWatcher? watcher))
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _localWatchers.Remove(path);
            LogService.Info($"Stopped watching local directory: {path}");
        }
    }

    private void OnLocalFileSystemChanged(LocalFileViewModel item)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (!item.ChildrenLoaded || !item.IsExpanded)
            {
                return;
            }

            try
            {
                HashSet<string> expandedChildPaths = item.Children
                    .Where(c => c.IsDirectory && c.IsExpanded)
                    .Select(c => c.FullPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                LoadLocalChildren(item);

                foreach (LocalFileViewModel child in item.Children)
                {
                    if (expandedChildPaths.Contains(child.FullPath))
                    {
                        child.IsExpanded = true;
                    }
                }

                LogService.Info($"Refreshed local directory after change: {item.FullPath}");
            }
            catch (Exception ex)
            {
                LogService.Warn($"Could not refresh local directory {item.FullPath}: {ex.Message}");
            }
        });
    }

    // ---------- Remote selection / drag-drop ----------

    public void OnRemoteMouseDown(RemoteFileViewModel? item, ModifierKeys modifiers)
    {
        if (item == null)
        {
            return;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            item.IsHighlighted = !item.IsHighlighted;
            _selectionAnchor = item;
        }
        else if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            SelectRange(item);
        }
        else
        {
            bool wasHighlighted = item.IsHighlighted;
            ClearHighlight();

            if (!wasHighlighted || GetHighlightedRemoteItems().Count == 0)
            {
                item.IsHighlighted = true;
            }

            _selectionAnchor = item;
        }
    }

    private void ClearHighlight()
    {
        foreach (RemoteFileViewModel root in RemoteRoots)
        {
            SetHighlight(root, false);
        }
    }

    private void SetHighlight(RemoteFileViewModel item, bool value)
    {
        item.IsHighlighted = value;

        foreach (RemoteFileViewModel child in item.Children)
        {
            SetHighlight(child, value);
        }
    }

    private List<RemoteFileViewModel> FlattenVisible()
    {
        List<RemoteFileViewModel> flattened = new List<RemoteFileViewModel>();

        foreach (RemoteFileViewModel root in RemoteRoots)
        {
            Flatten(root, flattened);
        }

        return flattened;
    }

    private void Flatten(RemoteFileViewModel item, List<RemoteFileViewModel> output)
    {
        output.Add(item);

        foreach (RemoteFileViewModel child in item.Children)
        {
            Flatten(child, output);
        }
    }

    private void SelectRange(RemoteFileViewModel clickedItem)
    {
        List<RemoteFileViewModel> flattened = FlattenVisible();

        int anchorIndex = _selectionAnchor == null ? -1 : flattened.IndexOf(_selectionAnchor);
        int clickedIndex = flattened.IndexOf(clickedItem);

        if (anchorIndex < 0 || clickedIndex < 0)
        {
            ClearHighlight();
            clickedItem.IsHighlighted = true;
            _selectionAnchor = clickedItem;
            return;
        }

        ClearHighlight();

        int start = Math.Min(anchorIndex, clickedIndex);
        int end = Math.Max(anchorIndex, clickedIndex);

        for (int i = start; i <= end; i++)
        {
            flattened[i].IsHighlighted = true;
        }
    }

    public List<RemoteFileViewModel> GetHighlightedRemoteItems()
    {
        return FlattenVisible().Where(x => x.IsHighlighted).ToList();
    }

    public void HandleRemoteDrop(List<RemoteFileViewModel> items, LocalFileViewModel? target)
    {
        if (target == null)
        {
            target = LastLocalFolder;
        }

        if (target == null || !target.IsDirectory)
        {
            _dialogs.ShowInformation("Download", "Drop the items on a local folder.");
            return;
        }

        foreach (RemoteFileViewModel item in items)
        {
            if (item.IsDirectory)
            {
                TransferItemViewModel placeholder = AddScanningPlaceholder(item, target);
                _ = ScanDirectoryInBackgroundAsync(item, target, placeholder);
            }
            else
            {
                string localPath = Path.Combine(target.FullPath, item.Name);
                StartDownload(item.RemoteEntry, localPath);
            }
        }

        OnPropertyChanged(nameof(QueueCountDisplay));
        OnPropertyChanged(nameof(TotalSizeDisplay));
        SaveQueue();
    }

    public void OnLocalMouseDown(LocalFileViewModel item, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            item.IsHighlighted = !item.IsHighlighted;
        }
        else if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            // For local items, shift-select is less critical; just highlight
            item.IsHighlighted = true;
        }
        else
        {
            bool wasHighlighted = item.IsHighlighted;
            ClearLocalHighlight();

            if (!wasHighlighted || GetHighlightedLocalItems().Count == 0)
            {
                item.IsHighlighted = true;
            }
        }
    }

    private void ClearLocalHighlight()
    {
        foreach (LocalFileViewModel root in LocalRoots)
        {
            SetLocalHighlight(root, false);
        }
    }

    private void SetLocalHighlight(LocalFileViewModel item, bool value)
    {
        item.IsHighlighted = value;

        foreach (LocalFileViewModel child in item.Children)
        {
            SetLocalHighlight(child, value);
        }
    }

    private List<LocalFileViewModel> FlattenLocalVisible()
    {
        List<LocalFileViewModel> flattened = new List<LocalFileViewModel>();

        foreach (LocalFileViewModel root in LocalRoots)
        {
            FlattenLocal(root, flattened);
        }

        return flattened;
    }

    private void FlattenLocal(LocalFileViewModel item, List<LocalFileViewModel> output)
    {
        output.Add(item);

        foreach (LocalFileViewModel child in item.Children)
        {
            FlattenLocal(child, output);
        }
    }

    public List<LocalFileViewModel> GetHighlightedLocalItems()
    {
        return FlattenLocalVisible().Where(x => x.IsHighlighted).ToList();
    }

    public void HandleLocalDrop(List<LocalFileViewModel> items, RemoteFileViewModel? target)
    {
        RemoteFileViewModel? directory = target?.IsDirectory == true ? target : target?.RemoteParent;

        if (directory == null)
        {
            _dialogs.ShowInformation("Upload", "Drop the items on a remote folder.");
            return;
        }

        foreach (LocalFileViewModel item in items)
        {
            if (item.IsDirectory)
            {
                // For directories, scan local files and upload each
                UploadLocalDirectory(item, directory);
            }
            else
            {
                string remotePath = directory.FullPath == "/" ? "/" + item.Name : directory.FullPath + "/" + item.Name;

                TransferEntry entry = new TransferEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    FileName = item.Name,
                    RemotePath = remotePath,
                    LocalPath = item.FullPath,
                    TotalLength = item.Size,
                    Direction = TransferDirection.Upload
                };

                TransferItemViewModel itemVm = new TransferItemViewModel(entry);
                Downloads.Add(itemVm);
                StartUpload(entry, itemVm, item.FullPath, remotePath);
            }
        }

        OnPropertyChanged(nameof(QueueCountDisplay));
        OnPropertyChanged(nameof(TotalSizeDisplay));
        SaveQueue();
    }

    private void UploadLocalDirectory(LocalFileViewModel directory, RemoteFileViewModel target)
    {
        try
        {
            string[] files = Directory.GetFiles(directory.FullPath);

            string remoteDir = target.FullPath == "/" ? "/" + directory.Name : target.FullPath + "/" + directory.Name;
            _sftp.CreateDirectory(remoteDir);

            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                string remotePath = remoteDir == "/" ? "/" + fileName : remoteDir + "/" + fileName;
                long fileSize = new FileInfo(file).Length;

                TransferEntry entry = new TransferEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    FileName = fileName,
                    RemotePath = remotePath,
                    LocalPath = file,
                    TotalLength = fileSize,
                    Direction = TransferDirection.Upload
                };

                TransferItemViewModel itemVm = new TransferItemViewModel(entry);
                Downloads.Add(itemVm);
                StartUpload(entry, itemVm, file, remotePath);
            }

            LogService.Info($"Uploaded directory contents: {directory.FullPath} -> {remoteDir}");
        }
        catch (Exception ex)
        {
            LogService.Error($"Upload directory failed: {ex}");
            _dialogs.ShowError("Upload error", ex.Message);
        }
    }

    private TransferItemViewModel AddScanningPlaceholder(RemoteFileViewModel directory, LocalFileViewModel target)
    {
        TransferEntry entry = new TransferEntry
        {
            Id = Guid.NewGuid().ToString(),
            FileName = directory.Name,
            RemotePath = directory.FullPath,
            LocalPath = target.FullPath,
            Status = "Scanning…",
            TotalLength = 0
        };

        TransferItemViewModel placeholder = new TransferItemViewModel(entry, directory.RemoteEntry);
        Downloads.Add(placeholder);

        LogService.Info($"Queued directory for background scan: {directory.FullPath}");
        return placeholder;
    }

    private async Task ScanDirectoryInBackgroundAsync(
        RemoteFileViewModel directory,
        LocalFileViewModel target,
        TransferItemViewModel placeholder)
    {
        List<RemoteFileEntry> remoteFiles;

        try
        {
            remoteFiles = await Task.Run(() => _sftp.ListFilesRecursive(directory.FullPath));
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                placeholder.Status = "Error";
                placeholder.ErrorMessage = ex.Message;
                SaveQueue();
            });

            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Downloads.Remove(placeholder);

            string prefix = directory.FullPath.TrimEnd('/') + "/";

            foreach (RemoteFileEntry remoteFile in remoteFiles)
            {
                string relative = remoteFile.FullPath[prefix.Length..];
                string localPath = Path.Combine(
                    target.FullPath,
                    directory.Name,
                    relative.Replace('/', Path.DirectorySeparatorChar));

                StartDownload(remoteFile, localPath);
            }

            OnPropertyChanged(nameof(QueueCountDisplay));
            OnPropertyChanged(nameof(TotalSizeDisplay));
            SaveQueue();
            LogService.Info($"Queued {remoteFiles.Count} file(s) from {directory.FullPath}.");
        }, DispatcherPriority.Background);
    }

    // ---------- Downloads ----------

    private void StartDownload(RemoteFileEntry remoteFile, string localPath)
    {
        bool exists = Downloads.Any(x =>
            x.RemotePath.Equals(remoteFile.FullPath, StringComparison.OrdinalIgnoreCase) &&
            x.LocalPath.Equals(localPath, StringComparison.OrdinalIgnoreCase));

        if (exists)
        {
            return;
        }

        TransferEntry entry = new TransferEntry
        {
            Id = Guid.NewGuid().ToString(),
            FileName = Path.GetFileName(localPath),
            RemotePath = remoteFile.FullPath,
            LocalPath = localPath,
            Status = "Queued",
            TotalLength = remoteFile.Size
        };

        TransferItemViewModel item = new TransferItemViewModel(entry, remoteFile);
        Downloads.Add(item);

        _pendingQueue.Add(item);
        StartNextQueuedDownload();
    }

    private void StartNextQueuedDownload()
    {
        int max = Math.Clamp(_settings.MaxConcurrentDownloads, 1, 8);

        while (_activeDownloadIds.Count < max && _pendingQueue.Count > 0)
        {
            TransferItemViewModel next = _pendingQueue[0];
            _pendingQueue.RemoveAt(0);

            if (next.IsFinished || Downloads.Contains(next) == false)
            {
                continue;
            }

            CancellationTokenSource cts = new CancellationTokenSource();
            _cancellationTokens[next.Entry.Id] = cts;
            _activeDownloadIds.Add(next.Entry.Id);
            next.Status = "Active";

            _ = RunDownloadAsync(next, cts.Token);
        }
    }

    private async Task RunDownloadAsync(TransferItemViewModel item, CancellationToken token)
    {
        try
        {
            await _downloader.DownloadAsync(_settings, item.RemoteFile, item.LocalPath, _settings.ParallelConnections, item.Entry, token);

            item.SyncStatus();
            item.RefreshProgress();
        }
        catch (OperationCanceledException)
        {
            item.Status = "Paused";
            item.DownloadSpeed = 0;
            LogService.Info($"Download paused: {item.RemotePath}");
        }
        catch (Exception ex)
        {
            item.Status = "Error";
            item.ErrorMessage = ex.Message;
            item.DownloadSpeed = 0;
            LogService.Error($"Download failed for {item.RemotePath}: {ex}");
        }
        finally
        {
            _activeDownloadIds.Remove(item.Entry.Id);
            _cancellationTokens.Remove(item.Entry.Id);

            OnPropertyChanged(nameof(TotalSpeedDisplay));
            OnPropertyChanged(nameof(QueueCountDisplay));
            OnPropertyChanged(nameof(TotalSizeDisplay));
            SaveQueue();
            StartNextQueuedDownload();
        }
    }

    private void LoadPersistedDownloads()
    {
        List<TransferEntry> persisted = TransferQueueService.Load(_currentConnectionKey);
        int restored = 0;

        foreach (TransferEntry entry in persisted)
        {
            bool exists = Downloads.Any(x =>
                x.RemotePath.Equals(entry.RemotePath, StringComparison.OrdinalIgnoreCase) &&
                x.LocalPath.Equals(entry.LocalPath, StringComparison.OrdinalIgnoreCase));

            if (exists)
            {
                continue;
            }

            TransferItemViewModel item = new TransferItemViewModel(entry);
            Downloads.Add(item);
            _pendingQueue.Add(item);
            restored++;
        }

        if (restored > 0)
        {
            LogService.Info($"Restored {restored} unfinished download(s) for this connection.");
        }
    }

    private void SaveQueue()
    {
        if (string.IsNullOrWhiteSpace(_currentConnectionKey))
        {
            return;
        }

        List<TransferEntry> snapshot = Downloads.Select(x => x.Entry).ToList();
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

    private DateTime _lastQueueSave = DateTime.MinValue;

    private void PollDownloads(object? sender, EventArgs e)
    {
        bool changed = false;

        foreach (TransferItemViewModel item in Downloads.ToList())
        {
            long completed = item.CompletedLength;
            long delta = completed - item.LastPollCompleted;

            if (delta != 0)
            {
                changed = true;
            }

            item.DownloadSpeed = (long)(delta / _pollTimer.Interval.TotalSeconds);
            item.LastPollCompleted = completed;
            item.RefreshProgress();
        }

        if (changed)
        {
            OnPropertyChanged(nameof(TotalSpeedDisplay));
            OnPropertyChanged(nameof(TotalSizeDisplay));

            if ((DateTime.UtcNow - _lastQueueSave).TotalSeconds >= 5)
            {
                _lastQueueSave = DateTime.UtcNow;
                SaveQueue();
            }
        }
    }

    public void PauseDownload(TransferItemViewModel item)
    {
        if (item.Status.Equals("Queued", StringComparison.OrdinalIgnoreCase))
        {
            _pendingQueue.Remove(item);
            item.Status = "Paused";
            LogService.Info($"Removed queued download from queue: {item.RemotePath}");
            OnPropertyChanged(nameof(TotalSpeedDisplay));
            OnPropertyChanged(nameof(QueueCountDisplay));
            OnPropertyChanged(nameof(TotalSizeDisplay));
            SaveQueue();
            return;
        }

        if (_cancellationTokens.TryGetValue(item.Entry.Id, out CancellationTokenSource? cts))
        {
            cts.Cancel();
            item.Status = "Paused";
            item.DownloadSpeed = 0;
            LogService.Info($"Paused download: {item.RemotePath}");
            OnPropertyChanged(nameof(TotalSpeedDisplay));
            OnPropertyChanged(nameof(QueueCountDisplay));
            OnPropertyChanged(nameof(TotalSizeDisplay));
            SaveQueue();
        }
    }

    public void ResumeDownload(TransferItemViewModel item)
    {
        if (item.IsFinished)
        {
            return;
        }

        if (!_sftp.IsConnected)
        {
            _dialogs.ShowInformation("Resume", "Connect to the SFTP server first, then resume the transfer.");
            return;
        }

        if (item.Entry.Direction == TransferDirection.Upload)
        {
            if (_activeDownloadIds.Contains(item.Entry.Id))
            {
                return;
            }

            StartUpload(
                item.Entry,
                item,
                item.LocalPath,
                item.RemotePath,
                resume: true);

            return;
        }

        item.Status = "Queued";

        if (!_pendingQueue.Contains(item))
        {
            _pendingQueue.Add(item);
        }

        StartNextQueuedDownload();
    }

    public void RemoveDownload(TransferItemViewModel item)
    {
        _pendingQueue.Remove(item);

        if (_cancellationTokens.TryGetValue(item.Entry.Id, out CancellationTokenSource? cts))
        {
            cts.Cancel();
            _cancellationTokens.Remove(item.Entry.Id);
        }

        Task.Delay(300).ContinueWith(_ =>
        {
            try
            {
                string tempPath = ParallelSftpDownloadService.GetTempFilePath(item.LocalPath);

                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                    LogService.Info($"Deleted temporary download file: {tempPath}");
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Could not delete temporary download file for {item.RemotePath}: {ex.Message}");
            }
        });

        Downloads.Remove(item);
        _activeDownloadIds.Remove(item.Entry.Id);
        OnPropertyChanged(nameof(TotalSpeedDisplay));
        OnPropertyChanged(nameof(QueueCountDisplay));
        OnPropertyChanged(nameof(TotalSizeDisplay));
        SaveQueue();
        StartNextQueuedDownload();
        LogService.Info($"Removed download from list: {item.RemotePath}");
    }

    public void ClearQueue()
    {
        List<TransferItemViewModel> items = Downloads.ToList();

        foreach (TransferItemViewModel item in items)
        {
            _pendingQueue.Remove(item);

            if (item.Status.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
                _cancellationTokens.TryGetValue(item.Entry.Id, out CancellationTokenSource? cts))
            {
                cts.Cancel();
                item.Status = "Paused";
            }
        }

        Downloads.Clear();

        foreach (TransferItemViewModel item in items)
        {
            string localPath = item.LocalPath;

            Task.Delay(500).ContinueWith(_ =>
            {
                try
                {
                    string tempPath = ParallelSftpDownloadService.GetTempFilePath(localPath);

                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                        LogService.Info($"Deleted temporary download file: {tempPath}");
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warn($"Could not delete temporary download file: {ex.Message}");
                }
            });
        }

        OnPropertyChanged(nameof(QueueCountDisplay));
        OnPropertyChanged(nameof(TotalSizeDisplay));
        SaveQueue();
        LogService.Info($"Cleared download queue ({items.Count} item(s)). Active downloads were paused before removal.");
    }

    private void RemoveSelected(IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0)
        {
            return;
        }

        List<TransferItemViewModel> items = selectedItems
            .OfType<TransferItemViewModel>()
            .ToList();

        foreach (TransferItemViewModel item in items)
        {
            RemoveDownload(item);
        }
    }

    private void PauseAll()
    {
        foreach (TransferItemViewModel item in Downloads.Where(x => x.CanPause).ToList())
        {
            PauseDownload(item);
        }
    }

    private void ResumeAll()
    {
        foreach (TransferItemViewModel item in Downloads.Where(x => x.CanResume).ToList())
        {
            ResumeDownload(item);
        }
    }

    // ---------- Local item operations ----------

    public void NewFolder(LocalFileViewModel item)
    {
        LocalFileViewModel folder = item.IsDirectory ? item : item.LocalParent ?? item;

        if (!folder.IsDirectory)
        {
            return;
        }

        string? name = _dialogs.PromptText("New folder", "Folder name:", "New folder");

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            string newFolderPath = Path.Combine(folder.FullPath, name);
            Directory.CreateDirectory(newFolderPath);
            LogService.Info($"Created folder: {newFolderPath}");

            if (!folder.ChildrenLoaded)
            {
                LoadLocalChildren(folder);
            }
            else
            {
                LocalFileEntry entry = new LocalFileEntry
                {
                    Name = name,
                    FullPath = newFolderPath,
                    IsDirectory = true
                };

                folder.Children.Insert(0, new LocalFileViewModel(entry, folder));
            }
        }
        catch (Exception ex)
        {
            LogService.Error($"Create folder failed: {ex}");
            _dialogs.ShowError("Error", ex.Message);
        }
    }

    public void DeleteLocalItem(LocalFileViewModel item)
    {
        if (item.IsDrive)
        {
            _dialogs.ShowInformation("Delete", "A drive cannot be deleted.");
            return;
        }

        string message;
        if (item.IsDirectory)
        {
            message = $"Delete folder '{item.Name}' and all of its contents?";
        }
        else
        {
            message = $"Delete file '{item.Name}'?";
        }

        if (!_dialogs.Confirm("Confirm delete", message))
        {
            return;
        }

        try
        {
            LocalFileViewModel? parent = item.LocalParent;

            if (item.IsDirectory)
            {
                Directory.Delete(item.FullPath, true);
            }
            else
            {
                File.Delete(item.FullPath);
            }

            if (parent != null)
            {
                parent.Children.Remove(item);
            }

            LogService.Info($"Deleted local item: {item.FullPath}");
        }
        catch (Exception ex)
        {
            LogService.Error($"Delete failed: {ex}");
            _dialogs.ShowError("Delete error", ex.Message);
        }
    }

    public void DeleteRemoteItems(IList? items)
    {
        List<RemoteFileViewModel> remoteItems = items?.OfType<RemoteFileViewModel>().ToList() ?? new List<RemoteFileViewModel>();

        if (remoteItems.Count == 0)
        {
            return;
        }

        string message = remoteItems.Count == 1
            ? $"Delete remote item '{remoteItems[0].Name}'?"
            : $"Delete {remoteItems.Count} remote items?";

        if (!_dialogs.Confirm("Confirm delete", message))
        {
            return;
        }

        foreach (RemoteFileViewModel item in remoteItems.ToList())
        {
            try
            {
                _sftp.DeleteDirectory(item.FullPath);

                if (item.Parent != null)
                {
                    item.Parent.Children.Remove(item);
                }

                LogService.Info($"Deleted remote item: {item.FullPath}");
            }
            catch (Exception ex)
            {
                LogService.Error($"Delete remote item failed: {ex}");
                _dialogs.ShowError("Delete error", ex.Message);
            }
        }
    }

    public void DeleteLocalItems(IList? items)
    {
        List<LocalFileViewModel> localItems = items?.OfType<LocalFileViewModel>().Where(x => !x.IsDrive).ToList() ?? new List<LocalFileViewModel>();

        if (localItems.Count == 0)
        {
            return;
        }

        string message = localItems.Count == 1
            ? $"Delete '{localItems[0].Name}'?"
            : $"Delete {localItems.Count} local items?";

        if (!_dialogs.Confirm("Confirm delete", message))
        {
            return;
        }

        foreach (LocalFileViewModel item in localItems.ToList())
        {
            try
            {
                if (item.IsDirectory)
                {
                    Directory.Delete(item.FullPath, true);
                }
                else
                {
                    File.Delete(item.FullPath);
                }

                if (item.Parent != null)
                {
                    item.Parent.Children.Remove(item);
                }

                LogService.Info($"Deleted local item: {item.FullPath}");
            }
            catch (Exception ex)
            {
                LogService.Error($"Delete failed: {ex}");
                _dialogs.ShowError("Delete error", ex.Message);
            }
        }
    }

    private void UploadToRemote(RemoteFileViewModel target)
    {
        RemoteFileViewModel? directory = target.IsDirectory ? target : target.RemoteParent;

        if (directory == null)
        {
            directory = target;
        }

        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Upload file(s) to " + directory.Name,
            Multiselect = true,
            Filter = "All files (*.*)|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (string localPath in dialog.FileNames)
        {
            string fileName = Path.GetFileName(localPath);
            string remotePath = directory.FullPath == "/" ? "/" + fileName : directory.FullPath + "/" + fileName;

            TransferEntry entry = new TransferEntry
            {
                Id = Guid.NewGuid().ToString(),
                FileName = fileName,
                RemotePath = remotePath,
                LocalPath = localPath,
                TotalLength = new FileInfo(localPath).Length,
                Direction = TransferDirection.Upload
            };

            TransferItemViewModel itemVm = new TransferItemViewModel(entry);
            Downloads.Add(itemVm);
            StartUpload(entry, itemVm, localPath, remotePath);
        }
    }

    private void StartUpload(
        TransferEntry entry,
        TransferItemViewModel itemVm,
        string localPath,
        string remotePath,
        bool resume = false)
    {
        if (_activeDownloadIds.Contains(entry.Id))
        {
            return;
        }

        CancellationTokenSource cts = new CancellationTokenSource();
        _cancellationTokens[entry.Id] = cts;
        _activeDownloadIds.Add(entry.Id);

        itemVm.Status = "Active";

        long lastReported = 0;
        DateTime lastUpdate = DateTime.UtcNow;

        Task.Run(async () =>
        {
            try
            {
                long startOffset = 0;

                if (resume)
                {
                    startOffset = _sftp.GetRemoteFileSize(remotePath);
                    long localLength = new FileInfo(localPath).Length;

                    if (startOffset >= localLength)
                    {
                        itemVm.CompletedLength = localLength;
                        itemVm.RefreshProgress();
                        itemVm.Status = "Complete";
                        LogService.Info($"Upload already complete: {localPath} -> {remotePath}");
                        return;
                    }

                    itemVm.CompletedLength = startOffset;
                    itemVm.RefreshProgress();
                    LogService.Info($"Resuming upload at {startOffset} bytes: {localPath} -> {remotePath}");
                }

                await _sftp.UploadFileAsync(
                    localPath,
                    remotePath,
                    progress =>
                    {
                        long delta = progress - lastReported;
                        DateTime now = DateTime.UtcNow;

                        if (now - lastUpdate >= TimeSpan.FromMilliseconds(200) || progress >= entry.TotalLength)
                        {
                            itemVm.CompletedLength = progress;

                            double seconds = (now - lastUpdate).TotalSeconds;
                            if (seconds > 0)
                            {
                                itemVm.DownloadSpeed = (long)(delta / seconds);
                            }

                            lastReported = progress;
                            lastUpdate = now;
                            itemVm.RefreshProgress();
                        }
                    },
                    cts.Token,
                    startOffset);

                itemVm.Status = "Complete";
                LogService.Info($"Upload complete: {localPath} -> {remotePath}");
            }
            catch (OperationCanceledException)
            {
                itemVm.Status = "Paused";
                itemVm.DownloadSpeed = 0;
                LogService.Info($"Upload paused: {localPath}");
            }
            catch (Exception ex)
            {
                itemVm.Status = "Error";
                itemVm.ErrorMessage = ex.Message;
                itemVm.DownloadSpeed = 0;
                LogService.Error($"Upload failed: {ex}");
            }
            finally
            {
                _activeDownloadIds.Remove(entry.Id);
                _cancellationTokens.Remove(entry.Id);
            }
        }, cts.Token);
    }

    private void NewRemoteFolder(RemoteFileViewModel target)
    {
        RemoteFileViewModel? parent = target.IsDirectory ? target : target.RemoteParent;

        if (parent == null)
        {
            parent = target;
        }

        string? name = _dialogs.PromptText("New remote folder", "Folder name:", "New folder");

        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string remotePath = parent.FullPath == "/" ? "/" + name : parent.FullPath + "/" + name;

        try
        {
            _sftp.CreateDirectory(remotePath);

            if (parent.ChildrenLoaded)
            {
                List<RemoteFileEntry> entries = _sftp.ListDirectory(parent.FullPath);
                RemoteFileEntry? newEntry = entries.FirstOrDefault(e => e.FullPath == remotePath);

                if (newEntry != null)
                {
                    parent.Children.Insert(0, new RemoteFileViewModel(newEntry, parent));
                }
                else
                {
                    RefreshRemoteChildren(parent);
                }
            }
            else
            {
                parent.IsExpanded = true;
            }

            LogService.Info($"Created remote directory: {remotePath}");
        }
        catch (Exception ex)
        {
            LogService.Error($"Create remote directory failed: {ex}");
            _dialogs.ShowError("Error", ex.Message);
        }
    }

    private void DeleteRemoteItem(RemoteFileViewModel item)
    {
        string message;

        if (item.IsDirectory)
        {
            message = $"Delete remote directory '{item.Name}' and all of its contents?";
        }
        else
        {
            message = $"Delete remote file '{item.Name}'?";
        }

        if (!_dialogs.Confirm("Confirm delete", message))
        {
            return;
        }

        try
        {
            _sftp.DeleteDirectory(item.FullPath);

            RemoteFileViewModel? parent = item.RemoteParent;

            if (parent != null)
            {
                parent.Children.Remove(item);
            }

            LogService.Info($"Deleted remote item: {item.FullPath}");
        }
        catch (Exception ex)
        {
            LogService.Error($"Delete remote item failed: {ex}");
            _dialogs.ShowError("Delete error", ex.Message);
        }
    }

    private void RefreshRemoteChildren(RemoteFileViewModel item)
    {
        try
        {
            List<RemoteFileEntry> children = _sftp.ListDirectory(item.FullPath);
            item.Children.Clear();

            foreach (RemoteFileEntry child in children)
            {
                item.Children.Add(new RemoteFileViewModel(child, item));
            }

            item.ChildrenLoaded = true;
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to refresh {item.FullPath}: {ex}");
            _dialogs.ShowError("Error", ex.Message);
        }
    }

    private void SaveSettings()
    {
        EncryptedSettingsService.Save(_settings);
    }

    public void SaveStateOnClose()
    {
        _pollTimer.Stop();
        _remoteRefreshTimer.Stop();

        foreach (CancellationTokenSource cts in _cancellationTokens.Values)
        {
            cts.Cancel();
        }

        foreach (FileSystemWatcher watcher in _localWatchers.Values)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _localWatchers.Clear();
        SaveQueue();
        SaveSettings();
        _sftp.Dispose();
        LogService.Info("StreamZilla closed and SFTP connection was disposed.");
    }
}

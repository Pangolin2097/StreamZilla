using System.IO;
using StreamZilla.Models;

namespace StreamZilla.ViewModels;

public class DownloadItemViewModel : ViewModelBase
{
    private readonly TransferEntry _entry;
    private readonly RemoteFileEntry _remoteFile;
    private long _lastPollCompleted;
    private long _downloadSpeed;

    public DownloadItemViewModel(TransferEntry entry, RemoteFileEntry? remoteFile = null)
    {
        _entry = entry;

        if (remoteFile == null)
        {
            _remoteFile = new RemoteFileEntry
            {
                Name = Path.GetFileName(entry.RemotePath),
                FullPath = entry.RemotePath,
                IsDirectory = false,
                Size = entry.TotalLength
            };
        }
        else
        {
            _remoteFile = remoteFile;
        }
    }

    public TransferEntry Entry
    {
        get
        {
            return _entry;
        }
    }

    public RemoteFileEntry RemoteFile
    {
        get
        {
            return _remoteFile;
        }
    }

    public string Id
    {
        get
        {
            return _entry.Id;
        }
    }

    public string FileName
    {
        get
        {
            return _entry.FileName;
        }
    }

    public string RemotePath
    {
        get
        {
            return _entry.RemotePath;
        }
    }

    public string LocalPath
    {
        get
        {
            return _entry.LocalPath;
        }
    }

    public string Status
    {
        get
        {
            return _entry.Status;
        }
        set
        {
            _entry.Status = value;
            SyncStatus();
        }
    }

    public string ErrorMessage
    {
        get
        {
            return _entry.ErrorMessage;
        }
        set
        {
            if (_entry.ErrorMessage == value)
            {
                return;
            }

            _entry.ErrorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    public string DirectionDisplay
    {
        get
        {
            return _entry.Direction == TransferDirection.Upload ? "Upload" : "Download";
        }
    }

    public string StatusDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ErrorMessage))
            {
                return Status;
            }

            return $"{Status}: {ErrorMessage}";
        }
    }

    public bool IsFinished
    {
        get
        {
            return _entry.IsFinished;
        }
    }

    public bool IsDownloaded
    {
        get
        {
            return Status.Equals("Complete", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool CanPause
    {
        get
        {
            return Status.Equals("Active", StringComparison.OrdinalIgnoreCase) ||
                   Status.Equals("Queued", StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool CanResume
    {
        get
        {
            return Status.Equals("Paused", StringComparison.OrdinalIgnoreCase);
        }
    }

    public long TotalLength
    {
        get
        {
            return _entry.TotalLength;
        }
        set
        {
            if (_entry.TotalLength == value)
            {
                return;
            }

            _entry.TotalLength = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(SizeDisplay));
            OnPropertyChanged(nameof(EtaDisplay));
        }
    }

    public long CompletedLength
    {
        get
        {
            return _entry.CompletedLength;
        }
        set
        {
            _entry.CompletedLength = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(SizeDisplay));
        }
    }

    public long LastPollCompleted
    {
        get
        {
            return _lastPollCompleted;
        }
        set
        {
            _lastPollCompleted = value;
        }
    }

    public long DownloadSpeed
    {
        get
        {
            return _downloadSpeed;
        }
        set
        {
            if (_downloadSpeed == value)
            {
                return;
            }

            _downloadSpeed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedDisplay));
            OnPropertyChanged(nameof(EtaDisplay));
        }
    }

    public double ProgressPercent
    {
        get
        {
            if (TotalLength <= 0)
            {
                return 0;
            }

            return Math.Min(100, CompletedLength * 100.0 / TotalLength);
        }
    }

    public string SizeDisplay
    {
        get
        {
            if (TotalLength <= 0)
            {
                return "";
            }

            return $"{FileIconHelper.FormatSize(CompletedLength)} / {FileIconHelper.FormatSize(TotalLength)}";
        }
    }

    public string SpeedDisplay
    {
        get
        {
            if (DownloadSpeed <= 0)
            {
                return "";
            }

            if (DownloadSpeed >= 1_048_576)
            {
                return $"{DownloadSpeed / 1_048_576.0:F1} MB/s";
            }

            return $"{DownloadSpeed / 1024.0:F0} KB/s";
        }
    }

    public string EtaDisplay
    {
        get
        {
            if (DownloadSpeed <= 0 || TotalLength <= 0 || CompletedLength >= TotalLength)
            {
                return "";
            }

            double seconds = (TotalLength - CompletedLength) / (double)DownloadSpeed;
            TimeSpan eta = TimeSpan.FromSeconds(seconds);

            if (eta.TotalHours >= 1)
            {
                return $"{eta.Hours}h {eta.Minutes}m";
            }

            if (eta.TotalMinutes >= 1)
            {
                return $"{eta.Minutes}m {eta.Seconds}s";
            }

            return $"{eta.Seconds}s";
        }
    }

    public void SyncStatus()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    public void RefreshProgress()
    {
        OnPropertyChanged(nameof(CompletedLength));
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(EtaDisplay));

        if (CompletedLength >= TotalLength && TotalLength > 0 && !IsFinished)
        {
            Status = "Complete";
        }
    }
}

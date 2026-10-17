using System;
using System.ComponentModel;
using StreamZilla.Models;

namespace StreamZilla.ViewModels;

public sealed class TransferItemViewModel : ViewModelBase
{
    private readonly TransferEntry _entry;
    private long _lastPollCompleted;
    private long _transferSpeed;

    public TransferItemViewModel(TransferEntry entry)
    {
        _entry = entry;
    }

    public TransferEntry Entry => _entry;

    public string Id => _entry.Id;

    public string FileName => _entry.FileName;

    public string RemotePath => _entry.RemotePath;

    public string LocalPath => _entry.LocalPath;

    public string Status
    {
        get => _entry.Status;
        set
        {
            _entry.Status = value;
            SyncStatus();
        }
    }

    public string ErrorMessage
    {
        get => _entry.ErrorMessage;
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

    public string DirectionDisplay =>
        _entry.Direction == TransferDirection.Upload ? "Upload" : "Download";

    public string StatusDisplay =>
        string.IsNullOrWhiteSpace(ErrorMessage) ? Status : $"{Status}: {ErrorMessage}";

    public bool IsFinished => _entry.IsFinished;

    public bool IsComplete =>
        Status.Equals("Complete", StringComparison.OrdinalIgnoreCase);

    public bool CanPause =>
        Status.Equals("Active", StringComparison.OrdinalIgnoreCase) ||
        Status.Equals("Queued", StringComparison.OrdinalIgnoreCase);

    public bool CanResume =>
        Status.Equals("Paused", StringComparison.OrdinalIgnoreCase);

    public long TotalLength
    {
        get => _entry.TotalLength;
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
        get => _entry.CompletedLength;
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
        get => _lastPollCompleted;
        set => _lastPollCompleted = value;
    }

    public long TransferSpeed
    {
        get => _transferSpeed;
        set
        {
            if (_transferSpeed == value)
            {
                return;
            }

            _transferSpeed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedDisplay));
            OnPropertyChanged(nameof(EtaDisplay));
        }
    }

    public double ProgressPercent =>
        TotalLength <= 0 ? 0 : Math.Min(100, CompletedLength * 100.0 / TotalLength);

    public string SizeDisplay =>
        TotalLength <= 0
            ? ""
            : $"{FileIconHelper.FormatSize(CompletedLength)} / {FileIconHelper.FormatSize(TotalLength)}";

    public string SpeedDisplay
    {
        get
        {
            if (TransferSpeed <= 0)
            {
                return "";
            }

            return TransferSpeed >= 1_048_576
                ? $"{TransferSpeed / 1_048_576.0:F1} MB/s"
                : $"{TransferSpeed / 1024.0:F0} KB/s";
        }
    }

    public string EtaDisplay
    {
        get
        {
            if (TransferSpeed <= 0 || TotalLength <= 0 || CompletedLength >= TotalLength)
            {
                return "";
            }

            double seconds = (TotalLength - CompletedLength) / (double)TransferSpeed;
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
        OnPropertyChanged(nameof(IsComplete));
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

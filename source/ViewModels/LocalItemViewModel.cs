using System.Collections.ObjectModel;
using StreamZilla.Models;

namespace StreamZilla.ViewModels;

public class LocalItemViewModel : ViewModelBase
{
    private readonly LocalEntry _entry;
    private bool _isExpanded;
    private bool _childrenLoaded;
    private bool _isHighlighted;

    public LocalItemViewModel(LocalEntry entry, LocalItemViewModel? parent = null)
    {
        _entry = entry;
        Parent = parent;
    }

    public LocalItemViewModel? Parent { get; }

    public LocalEntry Entry
    {
        get
        {
            return _entry;
        }
    }

    public string Name
    {
        get
        {
            return _entry.Name;
        }
    }

    public string FullPath
    {
        get
        {
            return _entry.FullPath;
        }
    }

    public bool IsDrive
    {
        get
        {
            return _entry.IsDrive;
        }
    }

    public bool IsDirectory
    {
        get
        {
            return _entry.IsDirectory;
        }
    }

    public long Size
    {
        get
        {
            return _entry.Size;
        }
    }

    public bool IsExpanded
    {
        get
        {
            return _isExpanded;
        }
        set
        {
            SetProperty(ref _isExpanded, value);
        }
    }

    public bool ChildrenLoaded
    {
        get
        {
            return _childrenLoaded;
        }
        set
        {
            SetProperty(ref _childrenLoaded, value);
        }
    }

    public ObservableCollection<LocalItemViewModel> Children { get; } = new ObservableCollection<LocalItemViewModel>();

    public string Icon
    {
        get
        {
            if (IsDrive)
            {
                return "\uEDA2";
            }

            if (IsDirectory)
            {
                return "\uE8B7";
            }

            return FileIconHelper.GetFileIcon(Name);
        }
    }

    public string IconBrush
    {
        get
        {
            if (IsDrive)
            {
                return "#4CA6E8";
            }

            if (IsDirectory)
            {
                return "#F7C948";
            }

            return FileIconHelper.GetFileBrush(Name);
        }
    }

    public string SizeDisplay
    {
        get
        {
            if (IsDirectory)
            {
                return "";
            }

            return FileIconHelper.FormatSize(Size);
        }
    }

    public bool IsHighlighted
    {
        get
        {
            return _isHighlighted;
        }
        set
        {
            SetProperty(ref _isHighlighted, value);
        }
    }
}

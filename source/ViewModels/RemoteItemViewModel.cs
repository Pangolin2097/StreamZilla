using System.Collections.ObjectModel;
using StreamZilla.Models;

namespace StreamZilla.ViewModels;

public class RemoteItemViewModel : ViewModelBase
{
    private readonly RemoteEntry _entry;
    private bool _isHighlighted;
    private bool _isExpanded;
    private bool _childrenLoaded;

    public RemoteItemViewModel(RemoteEntry entry, RemoteItemViewModel? parent = null)
    {
        _entry = entry;
        Parent = parent;
    }

    public RemoteItemViewModel? Parent { get; }

    public RemoteEntry Entry
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

    public string Owner
    {
        get
        {
            return _entry.Owner;
        }
    }

    public string Permissions
    {
        get
        {
            return _entry.Permissions;
        }
    }

    public string ModifiedDisplay
    {
        get
        {
            if (_entry.LastModified == null)
            {
                return "";
            }

            return _entry.LastModified.Value.ToLocalTime().ToString("g");
        }
    }

    public string CreatedDisplay
    {
        get
        {
            if (_entry.Created == null)
            {
                return "";
            }

            return _entry.Created.Value.ToLocalTime().ToString("g");
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

    public ObservableCollection<RemoteItemViewModel> Children { get; } = new ObservableCollection<RemoteItemViewModel>();

    public string Icon
    {
        get
        {
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
}

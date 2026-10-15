using System.Collections;
using System.Collections.ObjectModel;
using System.Windows.Media;
using StreamZilla.Models;
using StreamZilla.Services;

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
        Children.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChildren));
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

    public string Group
    {
        get
        {
            return _entry.Group;
        }
    }

    public string OwnerGroupDisplay
    {
        get
        {
            string owner = string.IsNullOrEmpty(_entry.Owner) ? "" : _entry.Owner;
            string group = string.IsNullOrEmpty(_entry.Group) ? "" : _entry.Group;
            if (string.IsNullOrEmpty(owner) && string.IsNullOrEmpty(group)) return "";
            if (string.IsNullOrEmpty(group)) return owner;
            if (string.IsNullOrEmpty(owner)) return group;
            return $"{owner} {group}";
        }
    }

    public string FileTypeDisplay
    {
        get
        {
            if (IsDirectory)
            {
                return "Folder";
            }

            string ext = System.IO.Path.GetExtension(_entry.Name);
            return ext.Length > 0 ? ext.TrimStart('.') : "File";
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
            if (SetProperty(ref _childrenLoaded, value))
            {
                OnPropertyChanged(nameof(HasChildren));
            }
        }
    }

    public ObservableCollection<RemoteItemViewModel> Children { get; } = new ObservableCollection<RemoteItemViewModel>();

    public bool HasChildren
    {
        get
        {
            // Show expand arrow if not yet loaded (might have children) or if it has children
            return !ChildrenLoaded || Children.Count > 0;
        }
    }

    public IEnumerable<RemoteItemViewModel>? TreeChildren
    {
        get
        {
            return Children.Count > 0 ? Children : null;
        }
    }

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

    public ImageSource IconImage
    {
        get
        {
            return FileIconService.GetFileIcon(_entry.Name, IsDirectory);
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

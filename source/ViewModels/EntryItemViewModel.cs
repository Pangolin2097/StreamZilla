using System.Collections.ObjectModel;
using System.Windows.Media;
using StreamZilla.Models;
using StreamZilla.Services;

namespace StreamZilla.ViewModels;

public sealed class EntryItemViewModel : ViewModelBase
{
    private bool _isHighlighted;
    private bool _isExpanded;
    private bool _childrenLoaded;

    public EntryItemViewModel(Entry entry, EntryItemViewModel? parent = null)
    {
        Entry = entry;
        Parent = parent;

        Children.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChildren));
    }

    public Entry Entry { get; }

    public EntryItemViewModel? Parent { get; }

    public string Name => Entry.Name;

    public string FullPath => Entry.FullPath;

    public bool IsDirectory => Entry.IsDirectory;

    public long Size => Entry.Size;

    public bool IsDrive => Entry is LocalEntry localEntry && localEntry.IsDrive;

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => SetProperty(ref _isHighlighted, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool ChildrenLoaded
    {
        get => _childrenLoaded;
        set
        {
            if (SetProperty(ref _childrenLoaded, value))
            {
                OnPropertyChanged(nameof(HasChildren));
            }
        }
    }

    public ObservableCollection<EntryItemViewModel> Children { get; } = new();

    public bool HasChildren => !ChildrenLoaded || Children.Count > 0;

    public string ModifiedDisplay => Entry.Modified?.ToLocalTime().ToString("g") ?? "";

    public string CreatedDisplay => Entry.Created?.ToLocalTime().ToString("g") ?? "";

    public ImageSource IconImage =>
        FileIconService.GetFileIcon(Entry.FullPath, IsDirectory, IsDrive);

    public string SizeDisplay =>
        IsDirectory ? "" : FileIconHelper.FormatSize(Size);
}

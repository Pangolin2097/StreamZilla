using System.Collections.ObjectModel;
using System.Windows.Media;
using StreamZilla.Models;
using StreamZilla.Services;

namespace StreamZilla.ViewModels;

public class RemoteFileViewModel : FileViewModel
{
    public RemoteFileViewModel(RemoteFileEntry entry, RemoteFileViewModel? parent = null)
        : base(entry, parent)
    {
    }

    public RemoteFileEntry RemoteEntry => (RemoteFileEntry)Entry;
    public RemoteFileViewModel? RemoteParent => Parent as RemoteFileViewModel;

    public string OwnerGroupDisplay
    {
        get
        {
            string owner = string.IsNullOrEmpty(RemoteEntry.Owner) ? "" : RemoteEntry.Owner;
            string group = string.IsNullOrEmpty(RemoteEntry.Group) ? "" : RemoteEntry.Group;
            if (string.IsNullOrEmpty(owner) && string.IsNullOrEmpty(group)) return "";
            if (string.IsNullOrEmpty(group)) return owner;
            if (string.IsNullOrEmpty(owner)) return group;
            return $"{owner} {group}";
        }
    }

    public string Permissions
    {
        get
        {
            return RemoteEntry.Permissions;
        }
    }
}

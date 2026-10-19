using System.Collections.ObjectModel;
using System.Windows.Media;
using StreamZilla.Models;
using StreamZilla.Services;

namespace StreamZilla.ViewModels;

public class LocalFileViewModel : FileViewModel
{
    public LocalFileViewModel(LocalFileEntry entry, LocalFileViewModel? parent = null)
        : base(entry, parent)
    {

    }

    public LocalFileEntry LocalEntry => (LocalFileEntry)Entry;
    public LocalFileViewModel? LocalParent => (LocalFileViewModel?)Parent;

    public bool IsDrive
    {
        get
        {
            return (Entry is LocalFileEntry localEntry) && localEntry.IsDrive;
        }
    }

    public override string FileTypeDisplay
    {
        get
        {
            if (IsDrive)
            {
                return "Drive";
            }

            return base.FileTypeDisplay;
        }
    }

    public override string Icon
    {
        get
        {
            if (IsDrive)
            {
                return "\uEDA2";
            }

            return base.Icon;
        }
    }

    public override ImageSource IconImage
    {
        get
        {
            return FileIconService.GetFileIcon(Entry.FullPath, IsDirectory, IsDrive);
        }
    }

    public override string IconBrush
    {
        get
        {
            if (IsDrive)
            {
                return "#4CA6E8";
            }

            return base.IconBrush;
        }
    }
}

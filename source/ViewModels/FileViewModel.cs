using StreamZilla.Models;
using StreamZilla.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace StreamZilla.ViewModels
{
    public abstract class FileViewModel : ViewModelBase
    {
        private readonly FileEntry _entry;
        private bool _isExpanded;
        private bool _childrenLoaded;
        private bool _isHighlighted;

        public FileViewModel(FileEntry entry, FileViewModel? parent = null)
        {
            _entry = entry;
            Parent = parent;
            Children.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasChildren));
        }

        public FileViewModel? Parent { get; }

        public FileEntry Entry
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

        public virtual string FileTypeDisplay
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

        public string ModifiedDisplay
        {
            get
            {
                return _entry.Modified.HasValue ? _entry.Modified.Value.ToString("g") : string.Empty;
            }
        }

        public DateTime? Modified
        {
            get
            {
                return _entry.Modified;
            }
        }

        public DateTime? Created
        {
            get
            {
                return _entry.Created;
            }
        }

        public string CreatedDisplay
        {
            get
            {
                return _entry.Created.HasValue ? _entry.Created.Value.ToString("g") : string.Empty;
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

        public ObservableCollection<FileViewModel> Children { get; } = new ObservableCollection<FileViewModel>();

        public bool HasChildren
        {
            get
            {
                // Show expand arrow if not yet loaded (might have children) or if it has children
                return !ChildrenLoaded || Children.Count > 0;
            }
        }

        public IEnumerable<FileViewModel>? TreeChildren
        {
            get
            {
                return Children.Count > 0 ? Children : null;
            }
        }

        public virtual string Icon
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

        public virtual ImageSource IconImage
        {
            get
            {
                return FileIconService.GetFileIcon(_entry.FullPath, IsDirectory);
            }
        }

        public virtual string IconBrush
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
}

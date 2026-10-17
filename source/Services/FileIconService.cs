using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StreamZilla.Services;

public static class FileIconService
{
    private static readonly object _lock = new object();
    private static readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource GetFileIcon(string fileName, bool isDirectory, bool isDrive = false)
    {
        string key;
        if (isDrive)
        {
            key = "drive";
        }
        else if (isDirectory)
        {
            key = "folder";
        }
        else
        {
            key = fileName.ToLowerInvariant();
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out ImageSource? cached))
            {
                return cached;
            }
        }

        ImageSource icon;

        if (isDrive)
        {
            icon = GetDriveIcon();
        }
        else if (isDirectory)
        {
            icon = GetFolderIcon();
        }
        else
        {
            icon = GetFileIconForFile(fileName);
        }

        lock (_lock)
        {
            _cache[key] = icon;
        }

        return icon;
    }

    private static ImageSource GetDriveIcon()
    {
        // Try ExtractAssociatedIcon on a drive path
        ImageSource? icon = GetIconFromPath("C:\\");
        if (icon != null)
        {
            return icon;
        }

        // Try SHGetFileInfo
        icon = GetShellIconFromPath("C:\\");
        if (icon != null)
        {
            return icon;
        }

        return CreateDriveIcon();
    }

    private static ImageSource GetFolderIcon()
    {
        // Try ExtractAssociatedIcon on a known folder
        ImageSource? icon = GetIconFromPath("C:\\Windows");
        if (icon != null)
        {
            return icon;
        }

        // Try SHGetFileInfo
        icon = GetShellIconFromPath("C:\\Windows");
        if (icon != null)
        {
            return icon;
        }

        return CreateFolderIcon();
    }

    private static ImageSource GetFileIconForFile(string fileName)
    {
        // If it's a real local file, use ExtractAssociatedIcon
        if (File.Exists(fileName))
        {
            ImageSource? icon = GetIconFromPath(fileName);
            if (icon != null)
            {
                return icon;
            }

            // Also try SHGetFileInfo for the real file
            icon = GetShellIconFromPath(fileName);
            if (icon != null)
            {
                return icon;
            }
        }

        // For remote files (name only) or when the above fails:
        // Use ExtractAssociatedIcon with a temp file to get the extension-specific icon
        string ext = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext))
        {
            ImageSource? icon = GetIconByExtension(ext);
            if (icon != null)
            {
                return icon;
            }
        }

        return CreateFileIcon();
    }

    /// <summary>
    /// Uses System.Drawing.Icon.ExtractAssociatedIcon to get the icon for a real file path.
    /// </summary>
    private static ImageSource? GetIconFromPath(string path)
    {
        try
        {
            System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null)
            {
                return null;
            }

            try
            {
                ImageSource source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                icon.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets an icon by file extension by creating a temp file with that extension
    /// and calling ExtractAssociatedIcon on it.
    /// </summary>
    private static ImageSource? GetIconByExtension(string ext)
    {
        string? tempFile = null;
        try
        {
            string tempDir = Path.GetTempPath();
            string tempName = Path.Combine(tempDir, $"szicon_{Guid.NewGuid():N}{ext}");
            tempFile = tempName;
            File.WriteAllText(tempName, "");

            System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(tempName);
            if (icon == null)
            {
                return null;
            }

            try
            {
                ImageSource source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                icon.Dispose();
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (tempFile != null && File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // Ignore cleanup failures
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x00000010;

    private static ImageSource? GetShellIconFromPath(string path)
    {
        SHFILEINFO shinfo = new SHFILEINFO();
        uint cbFileInfo = (uint)Marshal.SizeOf<SHFILEINFO>();

        IntPtr result = SHGetFileInfo(path, 0, ref shinfo, cbFileInfo, SHGFI_ICON);

        if (result == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                shinfo.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    private static ImageSource CreateDriveIcon()
    {
        DrawingGroup group = new DrawingGroup();

        PathGeometry pathGeometry = new PathGeometry();
        PathFigure figure = new PathFigure
        {
            IsClosed = true,
            StartPoint = new Point(4, 6),
            Segments = new PathSegmentCollection
            {
                new LineSegment(new Point(24, 6), true),
                new LineSegment(new Point(24, 14), true),
                new LineSegment(new Point(4, 14), true),
                new LineSegment(new Point(4, 6), true)
            }
        };
        pathGeometry.Figures.Add(figure);

        SolidColorBrush fillBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x8E, 0x23));
        Pen pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0x6A, 0x14)), 1);

        GeometryDrawing driveBody = new GeometryDrawing(fillBrush, pen, pathGeometry);
        group.Children.Add(driveBody);

        EllipseGeometry ledGeom = new EllipseGeometry(new Point(20, 10), 1.5, 1.5);
        GeometryDrawing led = new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x00)), null, ledGeom);
        group.Children.Add(led);

        DrawingImage image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static ImageSource CreateFolderIcon()
    {
        DrawingGroup group = new DrawingGroup();

        PathGeometry tabGeometry = new PathGeometry();
        PathFigure tabFigure = new PathFigure
        {
            IsClosed = true,
            StartPoint = new Point(3, 5),
            Segments = new PathSegmentCollection
            {
                new LineSegment(new Point(10, 5), true),
                new LineSegment(new Point(11, 7), true),
                new LineSegment(new Point(3, 7), true)
            }
        };
        tabGeometry.Figures.Add(tabFigure);

        SolidColorBrush tabBrush = new SolidColorBrush(Color.FromRgb(0xF7, 0xC9, 0x48));
        Pen pen = new Pen(new SolidColorBrush(Color.FromRgb(0xD4, 0xA8, 0x35)), 0.5);

        GeometryDrawing tab = new GeometryDrawing(tabBrush, pen, tabGeometry);
        group.Children.Add(tab);

        RectangleGeometry bodyGeom = new RectangleGeometry(new Rect(3, 7, 18, 10), 1, 1);
        GeometryDrawing body = new GeometryDrawing(tabBrush, pen, bodyGeom);
        group.Children.Add(body);

        DrawingImage image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static ImageSource CreateFileIcon()
    {
        DrawingGroup group = new DrawingGroup();

        PathGeometry pathGeometry = new PathGeometry();
        PathFigure figure = new PathFigure
        {
            IsClosed = true,
            StartPoint = new Point(5, 2),
            Segments = new PathSegmentCollection
            {
                new LineSegment(new Point(15, 2), true),
                new LineSegment(new Point(21, 8), true),
                new LineSegment(new Point(21, 22), true),
                new LineSegment(new Point(5, 22), true),
                new LineSegment(new Point(5, 2), true)
            }
        };
        pathGeometry.Figures.Add(figure);

        SolidColorBrush fillBrush = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
        Pen pen = new Pen(new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), 0.8);

        GeometryDrawing body = new GeometryDrawing(fillBrush, pen, pathGeometry);
        group.Children.Add(body);

        PathGeometry foldGeometry = new PathGeometry();
        PathFigure foldFigure = new PathFigure
        {
            IsClosed = true,
            StartPoint = new Point(15, 2),
            Segments = new PathSegmentCollection
            {
                new LineSegment(new Point(15, 8), true),
                new LineSegment(new Point(21, 8), true)
            }
        };
        foldGeometry.Figures.Add(foldFigure);

        SolidColorBrush foldBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        GeometryDrawing fold = new GeometryDrawing(foldBrush, pen, foldGeometry);
        group.Children.Add(fold);

        for (int i = 0; i < 3; i++)
        {
            double y = 11 + i * 3;
            double width = 10 - i * 2;
            RectangleGeometry lineGeom = new RectangleGeometry(new Rect(8, y, width, 1.2));
            GeometryDrawing line = new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null, lineGeom);
            group.Children.Add(line);
        }

        DrawingImage image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}

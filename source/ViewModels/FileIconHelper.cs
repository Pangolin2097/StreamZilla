using System.IO;

namespace StreamZilla.ViewModels;

public static class FileIconHelper
{
    public static string FormatSize(long bytes)
    {
        double tb = bytes / 1_099_511_627_776.0;
        double gb = bytes / 1_073_741_824.0;
        double mb = bytes / 1_048_576.0;
        double kb = bytes / 1024.0;

        if (tb >= 1)
        {
            return $"{tb:F2} TB";
        }

        if (gb >= 1)
        {
            return $"{gb:F2} GB";
        }

        if (mb >= 1)
        {
            return $"{mb:F1} MB";
        }

        if (kb >= 1)
        {
            return $"{kb:F1} KB";
        }

        return $"{bytes} B";
    }

    public static string GetFileIcon(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "\uE91B",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "\uE714",
            ".mp3" or ".flac" or ".wav" or ".ogg" or ".m4a" => "\uE8D6",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "\uE8B7",
            ".pdf" => "\uEA90",
            ".txt" or ".md" or ".log" or ".csv" => "\uE8A5",
            ".exe" or ".msi" => "\uE7F8",
            _ => "\uE8A5"
        };
    }

    public static string GetFileBrush(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "#7BC67B",
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "#E06C9F",
            ".mp3" or ".flac" or ".wav" or ".ogg" or ".m4a" => "#B07CE8",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "#E8A33D",
            ".pdf" => "#E86C5C",
            ".exe" or ".msi" => "#5CB8E8",
            _ => "#9AA0A6"
        };
    }
}

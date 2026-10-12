using System.IO;
using System.Text;

namespace StreamZilla.Services;

public static class LogService
{
    private static readonly object Lock = new object();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamZilla");

    private static readonly string LogFilePath = Path.Combine(LogDirectory, "streamzilla.log");

    public static string FilePath
    {
        get
        {
            return LogFilePath;
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message)
    {
        Write("ERROR", message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            lock (Lock)
            {
                File.AppendAllText(
                    LogFilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}

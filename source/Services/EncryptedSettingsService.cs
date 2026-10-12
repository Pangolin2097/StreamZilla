using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StreamZilla.Models;

namespace StreamZilla.Services;

public static class EncryptedSettingsService
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StreamZilla");

    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.bin");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            byte[] encrypted = File.ReadAllBytes(FilePath);
            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);

            return JsonSerializer.Deserialize<AppSettings>(Encoding.UTF8.GetString(plain)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);

        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        byte[] plain = Encoding.UTF8.GetBytes(json);
        byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);

        File.WriteAllBytes(FilePath, encrypted);
    }
}

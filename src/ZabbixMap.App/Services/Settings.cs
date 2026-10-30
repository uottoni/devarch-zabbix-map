using System.Text.Json;

namespace ZabbixMap.App.Services;

/// <summary>Persisted connection data. Passwords are never saved; the API token is, in a user-only file.</summary>
public sealed class Settings
{
    public string Url { get; set; } = "";
    public string User { get; set; } = "";
    public string Token { get; set; } = "";
    public bool IgnoreCertificateErrors { get; set; }
    public string LastMapId { get; set; } = "";

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZabbixMap", "settings.json");

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

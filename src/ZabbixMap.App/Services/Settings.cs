using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZabbixMap.App.Services;

/// <summary>Persisted connection preferences. Credentials live in <see cref="ISecretStore"/>, never here.</summary>
public sealed class Settings
{
    public string Url { get; set; } = "";
    public string User { get; set; } = "";
    public bool IgnoreCertificateErrors { get; set; }
    public string LastMapId { get; set; } = "";
    /// <summary>True when the stored secret is a session from a password login (it can expire), false for an API token.</summary>
    public bool SessionLogin { get; set; }

    /// <summary>Only read, to migrate tokens saved in plain text by earlier versions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; set; }

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZabbixMap");
    public static string FilePath => Path.Combine(Directory, "settings.json");

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

    public void Save() => WritePrivate(FilePath, JsonSerializer.Serialize(this));

    /// <summary>Writes a file readable only by the current user (permission set before the content goes in).</summary>
    public static void WritePrivate(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(path, content);
    }
}

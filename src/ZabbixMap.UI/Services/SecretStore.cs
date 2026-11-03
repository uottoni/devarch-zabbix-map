using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZabbixMap.UI.Services;

/// <summary>Keeps API tokens and session ids out of settings.json, keyed by server URL.</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string account);
    Task SetAsync(string account, string secret);
    Task DeleteAsync(string account);
}

public static class SecretStores
{
    public static ISecretStore CreateDefault()
    {
        if (OperatingSystem.IsBrowser()) return new MemorySecretStore();
        var file = new FileSecretStore(Path.Combine(Settings.Directory, "secrets.json"), protect: OperatingSystem.IsWindows());
        if (OperatingSystem.IsLinux()) return new CommandSecretStore(file, SecretToolCommands);
        if (OperatingSystem.IsMacOS()) return new CommandSecretStore(file, KeychainCommands);
        return file;
    }

    const string Service = "ZabbixMap";

    // libsecret CLI: talks to GNOME Keyring or KWallet through the Secret Service API. The secret goes through stdin.
    static readonly CommandSet SecretToolCommands = new(
        "secret-tool",
        account => ["lookup", "service", Service, "account", account],
        account => (["store", "--label=ZabbixMap " + account, "service", Service, "account", account], true),
        account => ["clear", "service", Service, "account", account]);

    // macOS Keychain. `security` only accepts the secret as an argument, so it is briefly visible to the user's own processes.
    static readonly CommandSet KeychainCommands = new(
        "security",
        account => ["find-generic-password", "-s", Service, "-a", account, "-w"],
        account => (["add-generic-password", "-U", "-s", Service, "-a", account, "-w", "{secret}"], false),
        account => ["delete-generic-password", "-s", Service, "-a", account]);
}

public sealed record CommandSet(
    string Program,
    Func<string, string[]> Lookup,
    Func<string, (string[] Args, bool SecretOnStdin)> Store,
    Func<string, string[]> Clear);

/// <summary>Session-only store, for the browser and tests.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public Task<string?> GetAsync(string account) => Task.FromResult(Values.GetValueOrDefault(account));

    public Task SetAsync(string account, string secret)
    {
        Values[account] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string account)
    {
        Values.Remove(account);
        return Task.CompletedTask;
    }
}

/// <summary>OS keyring through its CLI tool, falling back to the file store when the tool or keyring is unavailable.</summary>
public sealed class CommandSecretStore(FileSecretStore fallback, CommandSet commands) : ISecretStore
{
    // Long enough for the user to answer a keyring unlock prompt.
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<string?> GetAsync(string account)
    {
        var (ok, output) = await RunAsync(commands.Lookup(account), null);
        return ok && output.Length > 0 ? output : await fallback.GetAsync(account);
    }

    public async Task SetAsync(string account, string secret)
    {
        var (args, onStdin) = commands.Store(account);
        if (!onStdin) args = args.Select(a => a == "{secret}" ? secret : a).ToArray();
        var (ok, _) = await RunAsync(args, onStdin ? secret : null);
        if (ok) await fallback.DeleteAsync(account);
        else await fallback.SetAsync(account, secret);
    }

    public async Task DeleteAsync(string account)
    {
        await RunAsync(commands.Clear(account), null);
        await fallback.DeleteAsync(account);
    }

    async Task<(bool Ok, string Output)> RunAsync(string[] args, string? stdin)
    {
        var info = new ProcessStartInfo(commands.Program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info)!;
            if (stdin != null) await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
            using var cts = new CancellationTokenSource(Timeout);
            var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode == 0, output.TrimEnd('\r', '\n'));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
        {
            Trace.WriteLine($"Cofre de credenciais indisponível ({commands.Program}): {e.Message}");
            return (false, "");
        }
    }
}

/// <summary>User-only JSON file; on Windows the values are additionally encrypted with DPAPI for the current user.</summary>
public sealed class FileSecretStore(string path, bool protect) : ISecretStore
{
    public Task<string?> GetAsync(string account)
    {
        var value = Read().GetValueOrDefault(account);
        return Task.FromResult(value == null ? null : Unprotect(value));
    }

    public Task SetAsync(string account, string secret)
    {
        var all = Read();
        all[account] = Protect(secret);
        Write(all);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string account)
    {
        var all = Read();
        if (all.Remove(account)) Write(all);
        return Task.CompletedTask;
    }

    Dictionary<string, string> Read()
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    void Write(Dictionary<string, string> all)
    {
        Settings.WritePrivate(path, JsonSerializer.Serialize(all));
    }

    string Protect(string secret) => protect && OperatingSystem.IsWindows()
        ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser))
        : secret;

    string? Unprotect(string value)
    {
        if (!protect || !OperatingSystem.IsWindows()) return value;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

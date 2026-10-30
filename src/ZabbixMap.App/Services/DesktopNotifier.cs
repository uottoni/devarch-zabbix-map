using System.Diagnostics;

namespace ZabbixMap.App.Services;

/// <summary>Native notification through each OS's own tool. Text goes as arguments or environment, never through a shell.</summary>
public static class DesktopNotifier
{
    public static void Show(string title, string message)
    {
        try
        {
            var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
            if (OperatingSystem.IsLinux())
            {
                info.FileName = "notify-send";
                info.ArgumentList.Add("--urgency=critical");
                info.ArgumentList.Add("--app-name=ZabbixMap");
                info.ArgumentList.Add(title);
                info.ArgumentList.Add(message);
            }
            else if (OperatingSystem.IsMacOS())
            {
                info.FileName = "osascript";
                info.ArgumentList.Add("-e");
                info.ArgumentList.Add("on run argv\ndisplay notification (item 2 of argv) with title (item 1 of argv)\nend run");
                info.ArgumentList.Add(title);
                info.ArgumentList.Add(message);
            }
            else if (OperatingSystem.IsWindows())
            {
                info.FileName = "powershell";
                info.Environment["ZM_TITLE"] = title;
                info.Environment["ZM_TEXT"] = message;
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-WindowStyle");
                info.ArgumentList.Add("Hidden");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add("Add-Type -AssemblyName System.Windows.Forms; $n=New-Object System.Windows.Forms.NotifyIcon; " +
                    "$n.Icon=[System.Drawing.SystemIcons]::Error; $n.Visible=$true; $n.ShowBalloonTip(10000,$env:ZM_TITLE,$env:ZM_TEXT,'Error'); Start-Sleep 10; $n.Dispose()");
            }
            else return;
            Process.Start(info)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The tool may be missing; the tray icon and in-app toast still alert the user.
            Trace.WriteLine($"Notificação nativa indisponível: {e.Message}");
        }
    }
}

using System.Diagnostics;
using System.Net.Http;

namespace Mapivo.Core;

/// <summary>Downloads the setup .exe and hands off to it, then relaunches Mapivo.</summary>
public static class Updater
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mapivo-Updater/" + AppInfo.Version);
        return c;
    }

    public static async Task<string> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Invalid download url");

        var name = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = "Mapivo-Setup.exe";

        var dir = Path.Combine(Path.GetTempPath(), "MapivoUpdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, name);

        using var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(dest))
            await src.CopyToAsync(file, ct);

        return dest;
    }

    /// <summary>
    /// Runs the setup silently, then reopens this executable. A batch file is
    /// used so the relaunch survives this process exiting for the installer.
    /// </summary>
    public static void LaunchInstallAndRestart(string setupPath)
    {
        var exe = Environment.ProcessPath
                  ?? Path.Combine(AppContext.BaseDirectory, "Mapivo.exe");

        var bat = Path.Combine(Path.GetTempPath(), "mapivo-update-" + Guid.NewGuid().ToString("N") + ".cmd");
        var lines = new[]
        {
            "@echo off",
            "timeout /t 2 /nobreak >nul",
            $"\"{setupPath}\" /SILENT /NORESTART /CLOSEAPPLICATIONS /SUPPRESSMSGBOXES",
            $"start \"\" \"{exe}\"",
            $"del \"{setupPath}\" >nul 2>&1",
            $"del \"%~f0\" >nul 2>&1"
        };
        File.WriteAllLines(bat, lines);

        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }
}

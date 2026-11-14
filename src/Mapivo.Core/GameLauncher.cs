using System.Diagnostics;

namespace Mapivo.Core;

public static class GameLauncher
{
    public static string DllFor(PeArchitecture arch, string dllFolder, string? exePath = null)
    {
        var sub = arch == PeArchitecture.X64 ? "x64" : "x86";
        var dir = Path.Combine(dllFolder, sub);
        if (!Directory.Exists(dir)) dir = dllFolder;

        string name = "xinput1_3.dll";
        if (exePath != null)
        {
            var imports = PeArchitectureReader.FindXInputImports(
                PeArchitectureReader.ResolveImportSource(exePath));
            if (imports.Count > 0) name = imports[0];
        }
        return Path.Combine(dir, name);
    }

    /// <summary>True when at least one slot is bound to a physical pad.</summary>
    public static bool HasPad(GameProfile profile)
        => profile.Bindings.Any(b => !string.IsNullOrWhiteSpace(b.Physical));

    public static void WriteGameConfig(string exePath, GameProfile profile)
    {
        var dir = Path.GetDirectoryName(exePath);
        if (dir == null) return;
        var sb = new System.Text.StringBuilder();
        foreach (var b in profile.Bindings.OrderBy(b => b.VirtualSlot))
        {
            sb.AppendLine($"{b.VirtualSlot}.src={(string.IsNullOrEmpty(b.Physical) ? "-1" : b.Physical)}");
            sb.AppendLine($"{b.VirtualSlot}.vib={(b.VibrationEnabled ? 1 : 0)}");
            var map = PadDb.Defaults(b.Physical);
            foreach (var kv in b.Map ?? new Dictionary<string, string>())
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    map[kv.Key] = kv.Value;
            foreach (var kv in map)
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)
                    && kv.Value != kv.Key)
                    sb.AppendLine($"{b.VirtualSlot}.{kv.Key}={kv.Value}");
        }
        try { File.WriteAllText(Path.Combine(dir, "mapivo.ini"), sb.ToString()); }
        catch { }
    }

    /// <summary>
    /// Copies every proxy XInput DLL plus the mapping file into the game
    /// folder, so the profile keeps working even when Mapivo is not running.
    /// </summary>
    public static bool Install(string exePath, string dllPath, out string error)
    {
        error = "";
        if (!File.Exists(exePath)) { error = "Game exe not found"; return false; }
        if (!File.Exists(dllPath)) { error = "Hook DLL not found: " + dllPath; return false; }

        var gameDir = Path.GetDirectoryName(exePath);
        if (gameDir == null) { error = "Bad game path"; return false; }

        var srcDir = Path.GetDirectoryName(dllPath);
        if (srcDir == null) { error = "Bad hook path"; return false; }

        // Only the DLLs the exe actually imports. Dropping every xinput variant
        // plus dinput8.dll into a folder that only wanted one of them is what
        // some game loaders/anti-tamper reject at startup.
        // A launcher exe imports neither API, so look at its sibling game exe.
        var srcExe = PeArchitectureReader.ResolveImportSource(exePath);
        var wanted = new List<string>();
        foreach (var n in PeArchitectureReader.FindXInputImports(srcExe))
            wanted.Add(n);
        if (PeArchitectureReader.ImportsDll(srcExe, "dinput8.dll"))
            wanted.Add("dinput8.dll");
        if (wanted.Count == 0) wanted.Add(Path.GetFileName(dllPath));

        try
        {
            foreach (var n in wanted)
            {
                var src = Path.Combine(srcDir, n);
                if (!File.Exists(src)) { error = "Hook DLL not found: " + src; return false; }
                File.Copy(src, Path.Combine(gameDir, n), true);
            }
        }
        catch (Exception ex)
        {
            error = "Could not place hook DLLs (run as admin?): " + ex.Message;
            return false;
        }
        return true;
    }

    /// <summary>Installs the hook and mapping, then starts the game.</summary>
    public static bool Launch(string exePath, string dllPath, out string error)
    {
        if (!Install(exePath, dllPath, out error)) return false;

        var gameDir = Path.GetDirectoryName(exePath)!;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = gameDir,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = "Start failed: " + ex.Message;
            return false;
        }
    }

    public static bool RemoveHook(string exePath, out string error)
    {
        error = "";
        var gameDir = Path.GetDirectoryName(exePath);
        if (gameDir == null) { error = "Bad game path"; return false; }
        try
        {
            foreach (var f in Directory.EnumerateFiles(gameDir, "*.dll")
                         .Where(f => Path.GetFileName(f).StartsWith("xinput", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(Path.GetFileName(f), "dinput8.dll", StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(f);
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}

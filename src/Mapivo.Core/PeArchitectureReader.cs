using System.Text;

namespace Mapivo.Core;

public enum PeArchitecture { Unknown, X86, X64, Arm64 }

public static class PeArchitectureReader
{
    static readonly string[] KnownXInputDlls =
    {
        "xinput1_4.dll", "xinput1_3.dll", "xinput1_2.dll",
        "xinput1_1.dll", "xinput9_1_0.dll"
    };

    static bool TryReadHeaders(BinaryReader br, out ushort machine, out uint importRva, out List<(uint va, uint size, uint ptr)> sections)
    {
        machine = 0; importRva = 0; sections = new();
        if (br.ReadUInt16() != 0x5A4D) return false;
        br.BaseStream.Position = 0x3C;
        int pe = br.ReadInt32();
        br.BaseStream.Position = pe;
        if (br.ReadUInt32() != 0x00004550) return false;

        machine = br.ReadUInt16();
        ushort numSections = br.ReadUInt16();
        br.BaseStream.Position = pe + 20;
        ushort optSize = br.ReadUInt16();
        long optStart = pe + 24;

        br.BaseStream.Position = optStart + (machine == 0x8664 ? 108 : 92);
        uint numDirs = br.ReadUInt32();
        for (int i = 0; i < numDirs; i++)
        {
            uint rva = br.ReadUInt32();
            br.ReadUInt32();
            if (i == 1) importRva = rva;
        }

        br.BaseStream.Position = optStart + optSize;
        for (int i = 0; i < numSections; i++)
        {
            br.ReadBytes(8);
            uint vsize = br.ReadUInt32();
            uint va = br.ReadUInt32();
            uint rawSize = br.ReadUInt32();
            uint rawPtr = br.ReadUInt32();
            br.ReadBytes(16);
            sections.Add((va, Math.Max(rawSize, vsize), rawPtr));
        }
        return true;
    }

    static long RvaToOffset(List<(uint va, uint size, uint ptr)> sections, uint rva)
    {
        foreach (var (va, size, ptr) in sections)
            if (rva >= va && rva < va + size)
                return ptr + (rva - va);
        return -1;
    }

    public static PeArchitecture Detect(string exePath)
    {
        try
        {
            using var fs = File.OpenRead(exePath);
            using var br = new BinaryReader(fs);
            if (!TryReadHeaders(br, out var machine, out _, out _))
                return PeArchitecture.Unknown;
            return machine switch
            {
                0x014C => PeArchitecture.X86,
                0x8664 => PeArchitecture.X64,
                0xAA64 => PeArchitecture.Arm64,
                _ => PeArchitecture.Unknown
            };
        }
        catch { return PeArchitecture.Unknown; }
    }

    public static bool IsSupported(PeArchitecture arch) =>
        arch is PeArchitecture.X86 or PeArchitecture.X64;

    /// <summary>Every DLL name the exe statically imports, lowercase, encounter order.</summary>
    static List<string> FindImports(string exePath)
    {
        var found = new List<string>();
        try
        {
            using var fs = File.OpenRead(exePath);
            using var br = new BinaryReader(fs);
            if (!TryReadHeaders(br, out _, out uint importRva, out var sections) || importRva == 0)
                return found;

            long impOff = RvaToOffset(sections, importRva);
            if (impOff < 0) return found;

            while (true)
            {
                // IMPORT_DESCRIPTOR: OriginalFirstThunk(0), TimeDateStamp(4),
                // ForwarderChain(8), Name(12), FirstThunk(16).
                br.BaseStream.Position = impOff;
                br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt32();
                uint nameRva = br.ReadUInt32();
                uint firstThunk = br.ReadUInt32();
                if (nameRva == 0 && firstThunk == 0) break;

                long nameOff = RvaToOffset(sections, nameRva);
                if (nameOff >= 0)
                {
                    br.BaseStream.Position = nameOff;
                    var sb = new StringBuilder();
                    byte b;
                    while ((b = br.ReadByte()) != 0) sb.Append((char)b);
                    var name = sb.ToString().ToLowerInvariant();
                    if (!found.Contains(name))
                        found.Add(name);
                }
                impOff += 20;
            }
        }
        catch { }
        return found;
    }

    /// <summary>xinput DLL names the exe imports, lowercase, preferred order.</summary>
    public static List<string> FindXInputImports(string exePath)
        => FindImports(exePath)
            .Where(n => KnownXInputDlls.Contains(n))
            .OrderBy(n => Array.IndexOf(KnownXInputDlls, n))
            .ToList();

    /// <summary>True when the exe statically imports the named DLL (case-insensitive).</summary>
    public static bool ImportsDll(string exePath, string dllName)
        => FindImports(exePath).Contains(dllName.ToLowerInvariant());

    /// <summary>True when the file contains the given ASCII text, case-insensitive.
    /// Finds APIs a game loads by name at runtime (LoadLibrary/GetProcAddress)
    /// rather than through its import table.</summary>
    public static bool ReferencesString(string path, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return false;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var want = needle.ToLowerInvariant();
            for (int i = 0; i + want.Length <= bytes.Length; i++)
            {
                int k = 0;
                for (; k < want.Length; k++)
                {
                    byte c = bytes[i + k];
                    if (c >= 'A' && c <= 'Z') c = (byte)(c + 32);
                    if (c != (byte)want[k]) break;
                }
                if (k == want.Length) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>XInput DLL name the game references at runtime but does not
    /// statically import, in preferred order ("" when none). Games that load
    /// XInput by name, like PES2012 which names xinput1_1.dll, still use XInput
    /// but export no import entry for a proxy to sit on.</summary>
    public static string DynamicXInputReference(string exePath)
    {
        foreach (var n in KnownXInputDlls)
            if (ReferencesString(exePath, n))
                return n;
        return "";
    }

    static bool ImportsXInput(string exePath)
    {
        var imp = FindImports(exePath);
        for (int i = 0; i < imp.Count; i++)
            if (KnownXInputDlls.Contains(imp[i])) return true;
        return false;
    }

    static bool ImportsDirectInput(string exePath)
        => ImportsDll(exePath, "dinput8.dll");

    /// <summary>
    /// The exe whose imports matter for the hook. Games often ship a launcher
    /// (WWE2K15Launcher.exe) that imports no controller API while the actual
    /// game exe next to it does, so fall back to scanning siblings: first for
    /// an XInput consumer, then for a DirectInput one.
    /// </summary>
    public static string ResolveImportSource(string exePath)
    {
        if (ImportsXInput(exePath) || ImportsDirectInput(exePath)) return exePath;
        var dir = Path.GetDirectoryName(exePath);
        if (dir == null) return exePath;
        try
        {
            var exes = Directory.EnumerateFiles(dir, "*.exe")
                .Where(f => !string.Equals(f, exePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var xi = exes.FirstOrDefault(ImportsXInput);
            if (xi != null) return xi;
            var di = exes.FirstOrDefault(ImportsDirectInput);
            if (di != null) return di;
        }
        catch { }
        return exePath;
    }
}

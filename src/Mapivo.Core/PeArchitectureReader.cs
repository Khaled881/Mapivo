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

    /// <summary>xinput DLL names the exe imports, lowercase, preferred order.</summary>
    public static List<string> FindXInputImports(string exePath)
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
                br.BaseStream.Position = impOff;
                uint nameRva = br.ReadUInt32();
                br.ReadUInt32();
                br.ReadUInt32();
                uint firstThunk = br.ReadUInt32();
                br.ReadUInt32();
                if (nameRva == 0 && firstThunk == 0) break;

                long nameOff = RvaToOffset(sections, nameRva);
                if (nameOff >= 0)
                {
                    br.BaseStream.Position = nameOff;
                    var sb = new StringBuilder();
                    byte b;
                    while ((b = br.ReadByte()) != 0) sb.Append((char)b);
                    var name = sb.ToString().ToLowerInvariant();
                    if (KnownXInputDlls.Contains(name) && !found.Contains(name))
                        found.Add(name);
                }
                impOff += 20;
            }
        }
        catch { }
        return found.OrderBy(n => Array.IndexOf(KnownXInputDlls, n)).ToList();
    }
}

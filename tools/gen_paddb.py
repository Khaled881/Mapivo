import re

SRC = r"C:\Users\Administrator\Downloads\x360ce\Mapivo\tools\gamecontrollerdb.txt"
OUT = r"C:\Users\Administrator\Downloads\x360ce\Mapivo\src\Mapivo.Core\PadDb.cs"

KEYMAP = {
    "a": "a", "b": "b", "x": "x", "y": "y",
    "leftshoulder": "lb", "rightshoulder": "rb",
    "back": "back", "start": "start",
    "leftstick": "l3", "rightstick": "r3",
    "lefttrigger": "lt", "righttrigger": "rt",
    "dpup": "up", "dpdown": "down", "dpleft": "left", "dpright": "right",
}

rows = {}
with open(SRC, encoding="utf-8") as fh:
    for line in fh:
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split(",")
        if len(parts) < 3:
            continue
        guid = parts[0]
        if len(guid) != 32 or "platform:Windows" not in line:
            continue
        try:
            raw = bytes.fromhex(guid)
        except ValueError:
            continue
        vid = int.from_bytes(raw[4:6], "little")
        pid = int.from_bytes(raw[8:10], "little")
        if vid == 0 or pid == 0:
            continue
        key = "%04X_%04X" % (vid, pid)
        if key in rows:
            continue
        m = {}
        for field in parts[2:]:
            if ":" not in field:
                continue
            k, v = field.split(":", 1)
            tgt = KEYMAP.get(k)
            if not tgt or not re.fullmatch(r"b\d+", v):
                continue
            m[tgt] = v
        if "a" not in m or "b" not in m or "x" not in m or "y" not in m:
            continue
        rows[key] = m

order = ["a", "b", "x", "y", "lb", "rb", "back", "start", "l3", "r3",
         "up", "down", "left", "right", "lt", "rt"]

lines = []
for key in sorted(rows):
    m = rows[key]
    body = ",".join("%s=%s" % (t, m[t]) for t in order if t in m)
    lines.append('        ["%s"] = "%s",' % (key, body))

out = '''namespace Mapivo.Core;

/// <summary>
/// Per-device button maps derived from SDL's GameControllerDB (Windows rows).
/// DirectInput exposes HID buttons in an arbitrary, device-specific order, so
/// the only reliable way to label them is a per-VID/PID map -- the same data
/// SDL and x360ce use. Keys are "VVVV_PPPP" in upper-case hex, matching the
/// PadScanner token "jVVVV_PPPP".
/// </summary>
public static class PadDb
{
    // target -> physical source ("bN"), semicolon-free comma list.
    static readonly Dictionary<string, string> Raw = new(StringComparer.OrdinalIgnoreCase)
    {
%s
    };

    static readonly Dictionary<string, Dictionary<string, string>> Parsed = Build();

    static Dictionary<string, Dictionary<string, string>> Build()
    {
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Raw)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in kv.Value.Split(','))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                map[pair[..eq]] = pair[(eq + 1)..];
            }
            all[kv.Key] = map;
        }
        return all;
    }

    /// <summary>
    /// Default target -> source map for a pad token ("jVVVV_PPPP"), or an empty
    /// map when the device is not in the database.
    /// </summary>
    public static Dictionary<string, string> Defaults(string token)
    {
        if (string.IsNullOrEmpty(token) || token[0] != 'j' || token.Length < 4)
            return new Dictionary<string, string>();
        var key = token[1..].ToUpperInvariant();
        return Parsed.TryGetValue(key, out var map)
            ? new Dictionary<string, string>(map)
            : new Dictionary<string, string>();
    }

    public static int Count => Parsed.Count;
}
''' % "\n".join(lines)

with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
    fh.write(out)

print("devices:", len(rows))
print("bytes:", len(out))
print("sample 2563_0575:", rows.get("2563_0575"))
print("sample 045E_028E:", rows.get("045E_028E"))

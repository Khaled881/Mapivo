using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mapivo.Core;

public sealed class ControllerBinding
{
    public int VirtualSlot { get; set; }
    public string Physical { get; set; } = "";
    public string Name { get; set; } = "";
    public bool VibrationEnabled { get; set; } = true;
    public float LeftDeadzone { get; set; } = 0.08f;
    public float RightDeadzone { get; set; } = 0.08f;

    /// <summary>Virtual target -> physical source (e.g. "a" -> "b2"). Missing = identity.</summary>
    public Dictionary<string, string> Map { get; set; } = new();

    [JsonIgnore]
    public bool IsXInput => Physical.StartsWith("x") && Physical.Length == 2;

    [JsonIgnore]
    public int XInputIndex => IsXInput && int.TryParse(Physical[1..], out var i) ? i : -1;
}

public sealed class GameProfile
{
    public string Executable { get; set; } = "";
    public string GamePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<ControllerBinding> Bindings { get; set; } = new();

    public static GameProfile CreateDefault(string exePath)
    {
        var p = new GameProfile
        {
            Executable = Path.GetFileName(exePath),
            GamePath = exePath,
            DisplayName = Path.GetFileNameWithoutExtension(exePath)
        };
        for (int i = 0; i < 4; i++)
            p.Bindings.Add(new ControllerBinding { VirtualSlot = i, Physical = "" });
        return p;
    }
}

public sealed class ProfileStore
{
    public static string Root { get; } =
        Path.Combine(AppContext.BaseDirectory, "Profiles");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IReadOnlyList<GameProfile> All()
    {
        Directory.CreateDirectory(Root);
        var list = new List<GameProfile>();
        foreach (var f in Directory.EnumerateFiles(Root, "*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(f), Json);
                if (p != null) list.Add(p);
            }
            catch { }
        }
        return list.OrderBy(p => p.DisplayName).ToList();
    }

    static string PathFor(string exe) =>
        Path.Combine(Root, Sanitize(Path.GetFileNameWithoutExtension(exe)) + ".json");

    static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public static GameProfile LoadOrCreate(string exePath)
    {
        Directory.CreateDirectory(Root);
        var path = PathFor(exePath);
        if (File.Exists(path))
        {
            var p = JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(path), Json);
            if (p != null) return p;
        }
        return GameProfile.CreateDefault(exePath);
    }

    public static void Save(GameProfile profile)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(PathFor(profile.Executable),
            JsonSerializer.Serialize(profile, Json));
    }

    public static void Delete(GameProfile profile)
    {
        var path = PathFor(profile.Executable);
        if (File.Exists(path)) File.Delete(path);
    }
}

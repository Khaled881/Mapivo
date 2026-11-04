
namespace Mapivo.Core;

public sealed class AppSettings
{
    public bool AutoLaunchGame { get; set; } = true;
    public bool StartMinimized { get; set; }
    public string DllFolder { get; set; } = "DLL";
    public bool DarkMode { get; set; } = true;
    public string Language { get; set; } = "en";

    /// <summary>Games opened at least once, newest first. Lets the profile menu
    /// list a game before any profile exists and restores it on the next start.</summary>
    public List<string> KnownGames { get; set; } = new();

    public string LastGame { get; set; } = "";

    /// <summary>Virtual-driver rows, kept so the list survives a restart.</summary>
    public List<DriverSlot> DriverSlots { get; set; } = new();

    public sealed class DriverSlot
    {
        public int Index { get; set; }
        public string Token { get; set; } = "";
        public bool Enabled { get; set; }
        public Dictionary<string, string> Map { get; set; } = new();
    }

    static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                System.Text.Json.JsonSerializer.Serialize(this,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public string DllFolderFull =>
        Path.IsPathRooted(DllFolder) ? DllFolder : Path.Combine(AppContext.BaseDirectory, DllFolder);
}

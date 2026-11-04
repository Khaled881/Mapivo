namespace Mapivo.Core;

public sealed class AppLog
{
    public static AppLog Instance { get; } = new();
    public event Action<string>? LineAdded;

    public void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LineAdded?.Invoke(line);
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"mapivo-{DateTime.Now:yyyyMMdd}.log"),
                line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>Last N lines of today's app log, oldest first.</summary>
    public IReadOnlyList<string> Recent(int max)
    {
        var list = new List<string>();
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "logs",
                $"mapivo-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(file)) return list;
            var lines = File.ReadAllLines(file);
            for (int i = Math.Max(0, lines.Length - max); i < lines.Length; i++)
                if (lines[i].Length > 0) list.Add(lines[i]);
        }
        catch { }
        return list;
    }
}

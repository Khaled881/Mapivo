using System.Net.Http;
using System.Text.Json;

namespace Mapivo.Core;

public sealed record UpdateInfo(
    string LatestVersion,
    string ReleaseUrl,
    string? Notes,
    string? SetupUrl,
    bool IsNewer);

/// <summary>Asks the GitHub releases API for the latest published version.</summary>
public static class UpdateChecker
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mapivo-UpdateCheck/" + AppInfo.Version);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{AppInfo.RepoOwner}/{AppInfo.RepoName}/releases/latest";
        using var res = await Http.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
        var latest = NormalizeVersion(tag);
        var html = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;

        string? setup = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.StartsWith("Mapivo-Setup", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    setup = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    break;
                }
            }
        }

        return new UpdateInfo(latest, html, notes, setup, Compare(latest, AppInfo.Version) > 0);
    }

    static string NormalizeVersion(string tag) => tag.TrimStart('v', 'V').Trim();

    /// <summary>Dotted-numeric compare; anything unparsable counts as older.</summary>
    static int Compare(string a, string b)
    {
        var av = Parse(a);
        var bv = Parse(b);
        for (int i = 0; i < Math.Max(av.Length, bv.Length); i++)
        {
            int x = i < av.Length ? av[i] : 0;
            int y = i < bv.Length ? bv[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    static int[] Parse(string v)
    {
        var parts = v.Split('.', '-', '+');
        var nums = new List<int>();
        foreach (var p in parts)
        {
            int i = 0;
            while (i < p.Length && char.IsDigit(p[i])) i++;
            if (i == 0) break;
            if (!int.TryParse(p.AsSpan(0, i), out var n)) break;
            nums.Add(n);
            // A trailing letter marks a beta build ("1.0.1B"): rank it above the
            // bare number so the update check still offers it.
            if (i < p.Length)
            {
                char c = char.ToUpperInvariant(p[i]);
                nums.Add(c >= 'A' && c <= 'Z' ? c - 'A' + 1 : 0);
            }
        }
        return nums.ToArray();
    }
}

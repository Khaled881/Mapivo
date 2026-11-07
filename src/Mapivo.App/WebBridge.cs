using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Mapivo.Core;

namespace Mapivo.App;

/// <summary>Bridges the HTML UI to the core: receives JSON actions, returns JSON state.</summary>
public sealed class WebBridge
{
    readonly AppSettings _settings;
    GameProfile? _profile;
    string? _gamePath;
    PeArchitecture _arch;
    string? _dll;
    List<PhysicalPad> _pads = new();
    List<SlotState> _slots = new();
    readonly DriverEngine _driver = new();
    bool _driverRunning;

    public Action<Action>? OnUi { get; set; }
    public Action<string>? OnLog { get; set; }
    public Action? RequestShutdown { get; set; }

    public sealed class SlotState
    {
        public string Physical { get; set; } = "";
        public bool Vib { get; set; } = true;
        public Dictionary<string, string> Map { get; set; } = new();
    }

    public WebBridge(AppSettings settings) => _settings = settings;

    /// <summary>App shutdown: never leave a virtual pad behind.</summary>
    public void Shutdown()
    {
        try { _driver.Stop(); } catch { }
        _driverRunning = false;
    }

    public string Load()
    {
        _pads = PadScanner.Scan();
        _slots = Enumerable.Range(0, 4).Select(_ => new SlotState()).ToList();
        for (int i = 0; i < _pads.Count && i < 4; i++)
        {
            _slots[i].Physical = _pads[i].Token;
            // Seed the per-device button map (SDL gamecontrollerdb) so a
            // DirectInput pad is correct before the user maps anything. An
            // XInput pad already reports canonical buttons: "b8" there means
            // "the ninth raw button", which made Back and Start collapse onto
            // each other, so it gets no map at all.
            var tok = _pads[i].Token;
            if (tok.Length > 0 && tok[0] != 'x' && tok[0] != 'X')
                _slots[i].Map = PadDb.Defaults(tok);
        }
        DriverSeed();
        RestoreLastGame();
        var st = State();
        st["settings"] = SettingsNode();
        st["profiles"] = ProfilesNode();
        return st.ToJsonString();
    }

    public string Act(string payload)
    {
        JsonObject req;
        try { req = JsonNode.Parse(payload)?.AsObject() ?? new(); }
        catch { return Err("Bad request").ToJsonString(); }

        var action = req["action"]?.GetValue<string>() ?? "";
        try
        {
            var res = action switch
            {
                "rescan" => Rescan(),
                "browse" => Browse(),
                "launch" => LaunchGame(),
                "install" => InstallHook(),
                "checkGame" => CheckGame(),
                "unhook" => Unhook(),
                "saveSlots" => SaveSlots(req),
                "selectProfile" => SelectProfile(req),
                "saveProfile" => SaveProfile(req),
                "deleteProfile" => DeleteProfile(),
                "saveSettings" => SaveSettings(req),
                "browseDll" => BrowseDll(),
                "checkUpdate" => CheckUpdate(),
                "installUpdate" => InstallUpdate(req),
                "openUrl" => OpenUrl(req),
                "live" => Live(req),
                "driverMap" => DriverMap(req),
                "saveMap" => SaveMap(req),
                "testVib" => TestVib(req),
                "logs" => Logs(req),
                "driver" => DriverStatus(),
                "openDllFolder" => OpenDllFolder(),
                "driverAdd" => DriverAdd(),
                "driverRemove" => DriverRemove(req),
                "driverToggle" => DriverToggle(req),
                "driverSource" => DriverSource(req),
                "driverStart" => DriverStart(),
                "driverStop" => DriverStop(),
                _ => Err("Unknown action " + action)
            };
            return res.ToJsonString();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Write("Action " + action + " failed: " + ex.Message);
            return Err(ex.Message).ToJsonString();
        }
    }

    void RestoreLastGame()
    {
        var path = _settings.LastGame;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var prof = ProfileStore.All().FirstOrDefault(
            x => x.GamePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (prof == null || !prof.Enabled) return;

        _gamePath = path;
        _profile = prof;
        _arch = PeArchitectureReader.Detect(path);
        _dll = PeArchitectureReader.IsSupported(_arch)
            ? GameLauncher.DllFor(_arch, _settings.DllFolderFull, path)
            : null;
        LoadSlotsFrom(prof);
    }

    void RememberGame(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _settings.LastGame = path;
        _settings.KnownGames.RemoveAll(x => x.Equals(path, StringComparison.OrdinalIgnoreCase));
        _settings.KnownGames.Insert(0, path);
        if (_settings.KnownGames.Count > 20)
            _settings.KnownGames.RemoveRange(20, _settings.KnownGames.Count - 20);
        _settings.Save();
    }

    JsonObject Rescan()
    {
        _pads = PadScanner.Scan();
        var st = State();
        st["profiles"] = ProfilesNode();
        st["message"] = $"{_pads.Count} pad(s) detected";
        return Ok(st);
    }

    /// <summary>Device change arrived from Windows. Rescan and return full state
    /// only when the set of pads actually changed, so the UI can update at once
    /// without polling.</summary>
    public JsonObject? RescanIfChanged()
    {
        var before = _pads.Select(x => x.Token).ToList();
        var after = PadScanner.Scan();
        var afterTokens = after.Select(x => x.Token).ToList();
        if (before.Count == afterTokens.Count && before.SequenceEqual(afterTokens))
            return null;
        _pads = after;
        var st = State();
        st["profiles"] = ProfilesNode();
        st["settings"] = SettingsNode();
        return st;
    }

    JsonObject Browse()
    {
        var dlg = new OpenFileDialog { Filter = "Games (*.exe)|*.exe|All files (*.*)|*.*" };
        if (dlg.ShowDialog() != true) return new JsonObject { ["ok"] = false };

        _gamePath = dlg.FileName;
        RememberGame(_gamePath);
        _profile = ProfileStore.LoadOrCreate(_gamePath);
        _profile.GamePath = _gamePath;
        ProfileStore.Save(_profile);

        _arch = PeArchitectureReader.Detect(_gamePath);
        _dll = PeArchitectureReader.IsSupported(_arch)
            ? GameLauncher.DllFor(_arch, _settings.DllFolderFull, _gamePath)
            : null;

        LoadSlotsFrom(_profile);
        var st = State();
        st["profiles"] = ProfilesNode();
        st["message"] = "Loaded profile for " + _profile.DisplayName;
        AppLog.Instance.Write("Selected " + Path.GetFileName(_gamePath) + " (" + _arch + ")");
        return Ok(st);
    }

    JsonObject SelectProfile(JsonObject req)
    {
        var exe = req["executable"]?.GetValue<string>() ?? "";
        var path = req["gamePath"]?.GetValue<string>() ?? "";
        var p = ProfileStore.All().FirstOrDefault(x => x.Executable.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (p == null && path.Length > 0 && File.Exists(path))
        {
            p = ProfileStore.LoadOrCreate(path);
            p.GamePath = path;
            ProfileStore.Save(p);
        }
        if (p == null) return Err("Profile not found");

        if (!string.IsNullOrEmpty(p.GamePath)) RememberGame(p.GamePath);
        _profile = p;
        _gamePath = string.IsNullOrEmpty(p.GamePath) ? null : p.GamePath;
        if (_gamePath != null && File.Exists(_gamePath))
        {
            _arch = PeArchitectureReader.Detect(_gamePath);
            _dll = PeArchitectureReader.IsSupported(_arch)
                ? GameLauncher.DllFor(_arch, _settings.DllFolderFull, _gamePath)
                : null;
        }
        else { _arch = PeArchitecture.Unknown; _dll = null; }

        LoadSlotsFrom(p);
        return Ok(State());
    }

    JsonObject LaunchGame()
    {
        if (_gamePath == null || _profile == null) return Err("Select a game first");
        var dll = _dll ?? GameLauncher.DllFor(_arch, _settings.DllFolderFull, _gamePath);
        ApplySlotsTo(_profile);
        // Nothing configured means nothing to proxy: never drop hook DLLs and
        // mapivo.ini into a game folder for an empty profile.
        if (!GameLauncher.HasPad(_profile))
            return Err("No controller is mapped to a slot — set one up on the Mapping page first");
        ProfileStore.Save(_profile);
        GameLauncher.WriteGameConfig(_gamePath, _profile);

        if (GameLauncher.Launch(_gamePath, dll, out var err))
        {
            AppLog.Instance.Write("Launched " + Path.GetFileName(_gamePath));
            return new JsonObject { ["ok"] = true, ["message"] = "Launching " + _profile.DisplayName };
        }
        AppLog.Instance.Write("Launch failed: " + err);
        return Err(err);
    }

    static readonly (string Needle, string Label)[] AntiCheatSignals =
    {
        ("easyanticheat", "Easy Anti-Cheat"),
        ("easyanticheat_eos", "Easy Anti-Cheat (EOS)"),
        ("battleye", "BattlEye"),
        ("beservice", "BattlEye service"),
        ("start_protected_game", "BattlEye protected launcher"),
        ("vgc.exe", "Riot Vanguard"),
        ("vgk.sys", "Riot Vanguard driver"),
        ("vanguard", "Riot Vanguard"),
        ("ricochet", "Call of Duty Ricochet"),
        ("faceit", "FACEIT anti-cheat"),
        ("esea", "ESEA anti-cheat"),
        ("nprotect", "nProtect GameGuard"),
        ("gameguard", "nProtect GameGuard"),
        ("xigncode", "XignCode3"),
        ("punkbuster", "PunkBuster"),
        ("fairfight", "FairFight"),
        ("warden", "Blizzard Warden"),
        ("anticheat", "Anti-cheat component"),
        ("anti-cheat", "Anti-cheat component"),
        ("eac_launcher", "Easy Anti-Cheat launcher")
    };

    static readonly (string Needle, string Label)[] OnlineSignals =
    {
        ("eossdk", "Epic Online Services"),
        ("steam_api64", "Steam online API"),
        ("steam_api.dll", "Steam online API"),
        ("galaxy64", "GOG Galaxy online"),
        ("origin", "EA Origin online"),
        ("ea desktop", "EA online"),
        ("onlinefix", "Online fix/launcher")
    };

    JsonObject CheckGame()
    {
        if (_gamePath == null) return Err("Select a game first");
        var dir = Path.GetDirectoryName(_gamePath)!;
        var reasons = new List<string>();
        bool antiCheat = false, online = false;

        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 3,
                AttributesToSkip = 0
            };
            int seen = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", opts))
            {
                if (++seen > 20000) break;
                var name = Path.GetFileName(entry).ToLowerInvariant();
                foreach (var (needle, label) in AntiCheatSignals)
                {
                    if (name.Contains(needle) && !reasons.Contains(label))
                    {
                        reasons.Add(label);
                        antiCheat = true;
                    }
                }
                if (reasons.Count >= 6) break;
            }

            foreach (var (needle, label) in OnlineSignals)
            {
                if (reasons.Contains(label)) continue;
                if (Directory.EnumerateFiles(dir, "*" + needle + "*", new EnumerationOptions
                    { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 })
                    .Take(1).Any())
                {
                    reasons.Add(label);
                    online = true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Write("Game scan failed: " + ex.Message);
        }

        bool risk = antiCheat || online;
        return new JsonObject
        {
            ["ok"] = true,
            ["risk"] = risk,
            ["antiCheat"] = antiCheat,
            ["online"] = online,
            ["reasons"] = new JsonArray(reasons.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["game"] = Path.GetFileName(_gamePath)
        };
    }

    JsonObject InstallHook()
    {
        if (_gamePath == null || _profile == null) return Err("Select a game first");
        var dll = _dll ?? GameLauncher.DllFor(_arch, _settings.DllFolderFull, _gamePath);
        ApplySlotsTo(_profile);
        if (!GameLauncher.HasPad(_profile))
            return Err("No controller is mapped to a slot — set one up on the Mapping page first");
        ProfileStore.Save(_profile);

        if (!GameLauncher.Install(_gamePath, dll, out var err)) return Err(err);
        GameLauncher.WriteGameConfig(_gamePath, _profile);
        AppLog.Instance.Write("Hook installed for " + Path.GetFileName(_gamePath));
        return new JsonObject
        {
            ["ok"] = true,
            ["message"] = "Hook + settings copied — the game keeps this mapping without Mapivo"
        };
    }

    JsonObject Unhook()
    {
        if (_gamePath == null) return Err("Select a game first");
        return GameLauncher.RemoveHook(_gamePath, out var err)
            ? new JsonObject { ["ok"] = true, ["message"] = "Hook removed" }
            : Err(err);
    }

    JsonObject SaveSlots(JsonObject req)
    {
        if (_profile == null) return Err("Select a game first");
        ReadSlots(req);
        ApplySlotsTo(_profile);
        ProfileStore.Save(_profile);
        AppLog.Instance.Write("Saved slots for " + _profile.DisplayName);
        var st = State();
        st["profiles"] = ProfilesNode();
        st["message"] = "Slot map saved";
        return Ok(st);
    }

    JsonObject SaveProfile(JsonObject req)
    {
        if (_profile == null) return Err("Select a profile first");
        _profile.DisplayName = req["displayName"]?.GetValue<string>()?.Trim() is { Length: > 0 } n ? n : _profile.DisplayName;
        _profile.Enabled = req["enabled"]?.GetValue<bool>() ?? _profile.Enabled;
        ReadSlots(req);
        ApplySlotsTo(_profile);
        ProfileStore.Save(_profile);
        AppLog.Instance.Write("Saved profile " + _profile.DisplayName);
        var st = State();
        st["profiles"] = ProfilesNode();
        st["message"] = "Profile saved";
        return Ok(st);
    }

    JsonObject DeleteProfile()
    {
        if (_profile == null) return Err("Select a profile first");
        var deletedGame = _profile.GamePath;
        ProfileStore.Delete(_profile);
        AppLog.Instance.Write("Deleted profile " + _profile.DisplayName);
        // Keep the game listed so the dashboard shows it as "no profile" and
        // the user can give it a fresh one; only stop it being the active game.
        if (!string.IsNullOrEmpty(deletedGame)
            && string.Equals(_settings.LastGame, deletedGame, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastGame = "";
            _settings.Save();
        }
        _profile = null;
        _gamePath = null;
        _arch = PeArchitecture.Unknown;
        _dll = null;
        _slots = Enumerable.Range(0, 4).Select(_ => new SlotState()).ToList();
        var st = State();
        st["profiles"] = ProfilesNode();
        st["message"] = "Profile deleted";
        return Ok(st);
    }

    JsonObject SaveSettings(JsonObject req)
    {
        _settings.AutoLaunchGame = req["autoLaunchGame"]?.GetValue<bool>() ?? _settings.AutoLaunchGame;
        _settings.StartMinimized = req["startMinimized"]?.GetValue<bool>() ?? _settings.StartMinimized;
        _settings.Language = req["language"]?.GetValue<string>() ?? _settings.Language;
        _settings.Save();
        var st = State();
        st["settings"] = SettingsNode();
        st["message"] = "Settings saved";
        return Ok(st);
    }

    JsonObject CheckUpdate()
    {
        try
        {
            var info = UpdateChecker.CheckAsync().GetAwaiter().GetResult();
            return new JsonObject
            {
                ["ok"] = true,
                ["current"] = AppInfo.Version,
                ["latest"] = info.LatestVersion,
                ["isNewer"] = info.IsNewer,
                ["releaseUrl"] = info.ReleaseUrl,
                ["setupUrl"] = info.SetupUrl ?? "",
                ["notes"] = info.Notes ?? ""
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["ok"] = false,
                ["message"] = "Update check failed: " + ex.Message
            };
        }
    }

    JsonObject InstallUpdate(JsonObject req)
    {
        var url = req["url"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(url)) return Err("No download url");
        try
        {
            var setup = Updater.DownloadAsync(url).GetAwaiter().GetResult();
            Updater.LaunchInstallAndRestart(setup);
            RequestShutdown?.Invoke();
            return new JsonObject { ["ok"] = true, ["message"] = "Update downloaded. Mapivo will install and reopen." };
        }
        catch (Exception ex)
        {
            return Err("Update failed: " + ex.Message);
        }
    }

    JsonObject OpenUrl(JsonObject req)
    {
        var url = req["url"]?.GetValue<string>() ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return Err("Bad url");
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
            return Ok(new JsonObject());
        }
        catch (Exception ex)
        {
            return Err(ex.Message);
        }
    }

    JsonObject BrowseDll()
    {
        var dlg = new OpenFolderDialog();
        if (dlg.ShowDialog() != true) return new JsonObject { ["ok"] = false };
        _settings.DllFolder = dlg.FolderName;
        _settings.Save();
        var st = State();
        st["settings"] = SettingsNode();
        return Ok(st);
    }

    static readonly string[] Targets =
    {
        "a", "b", "x", "y", "lb", "rb", "back", "start", "l3", "r3",
        "up", "down", "left", "right", "lt", "rt",
        "lu", "ld", "ll", "lr", "ru", "rd", "rl", "rr"
    };

    JsonObject Live(JsonObject req)
    {
        // The virtual-driver page reads a row instead of a profile slot.
        if (req["driver"] != null)
            return DriverFrame(req["driver"]!.GetValue<int>());

        var token = req["token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token))
        {
            var slot = req["slot"]?.GetValue<int>() ?? -1;
            if (slot < 0 || slot >= _slots.Count) return Err("No pad for that slot");
            token = _slots[slot].Physical;
        }
        var slotIdx = req["slot"]?.GetValue<int>() ?? -1;
        var slotMap = slotIdx >= 0 && slotIdx < _slots.Count
            ? Merge(PadDb.Defaults(token ?? ""), _slots[slotIdx].Map)
            : new Dictionary<string, string>();
        var frame = PadReader.Read(token ?? "", slotMap);
        var buttons = new JsonArray();
        foreach (var b in frame.Buttons) buttons.Add(b);
        var raw = new JsonArray();
        foreach (var b in frame.RawButtons.Distinct().OrderBy(x => x)) raw.Add(b);

        // Names the hook uses as mapping sources. XInput pads only expose
        // canonical controls; DI pads also expose raw buttons as "bN".
        var isX = token != null && token.Length > 0 && (token[0] == 'x' || token[0] == 'X');
        var names = new JsonArray();
        foreach (var n in PadReader.ButtonNames) names.Add(n);
        foreach (var n in AxisSources) names.Add(n);
        if (!isX) for (int i = 0; i < 32; i++) names.Add("b" + i);
        var defaults = token != null ? PadDb.Defaults(token) : new Dictionary<string, string>();

        return new JsonObject
        {
            ["ok"] = true,
            ["connected"] = frame.Connected,
            ["error"] = frame.Error,
            ["vib"] = PadReader.IsBuzzing(token ?? "") ? 1 : 0,
            ["lx"] = frame.LX, ["ly"] = frame.LY, ["rx"] = frame.RX, ["ry"] = frame.RY,
            ["lt"] = frame.LT, ["rt"] = frame.RT,
            ["buttons"] = buttons,
            ["raw"] = raw,
            ["hasPov"] = frame.HasPov,
            ["names"] = names,
            ["defaults"] = JsonSerializer.SerializeToNode(defaults),
            ["targets"] = new JsonArray(Targets.Select(n => (JsonNode)n!).ToArray()),
            ["map"] = JsonSerializer.SerializeToNode(slotIdx >= 0 && slotIdx < _slots.Count
                ? Merge(token != null ? PadDb.Defaults(token) : new Dictionary<string, string>(), _slots[slotIdx].Map)
                : new Dictionary<string, string>())
        };
    }

    JsonObject DriverMap(JsonObject req)
    {
        int index = req["index"]?.GetValue<int>() ?? -1;
        var row = _settings.DriverSlots.FirstOrDefault(d => d.Index == index);
        if (row == null) return Err("Bad controller");
        var map = new Dictionary<string, string>();
        if (req["map"] is JsonObject obj)
            foreach (var kv in obj)
                if (kv.Value != null && kv.Value.ToString().Length > 0)
                    map[kv.Key] = kv.Value.ToString();
        row.Map = map;
        _settings.Save();
        var st = DriverStatus();
        st["message"] = "Controller saved";
        return st;
    }

    /// <summary>Live readout for one virtual-driver row.</summary>
    JsonObject DriverFrame(int index)
    {
        var row = _settings.DriverSlots.FirstOrDefault(d => d.Index == index);
        if (row == null) return Err("Bad controller");
        var map = new Dictionary<string, string>(PadDb.Defaults(row.Token));
        foreach (var kv in row.Map) map[kv.Key] = kv.Value;
        var frame = PadReader.Read(row.Token, map);
        var buttons = new JsonArray();
        foreach (var b in frame.Buttons) buttons.Add(b);
        var raw = new JsonArray();
        foreach (var b in frame.RawButtons.Distinct().OrderBy(x => x)) raw.Add(b);
        var isX = row.Token.Length > 0 && (row.Token[0] == 'x' || row.Token[0] == 'X');
        var names = new JsonArray();
        foreach (var n in PadReader.ButtonNames) names.Add(n);
        foreach (var n in AxisSources) names.Add(n);
        if (!isX) for (int i = 0; i < 32; i++) names.Add("b" + i);
        return new JsonObject
        {
            ["ok"] = true,
            ["connected"] = frame.Connected,
            ["error"] = frame.Error,
            ["vib"] = PadReader.IsBuzzing(row.Token) ? 1 : 0,
            ["lx"] = frame.LX, ["ly"] = frame.LY, ["rx"] = frame.RX, ["ry"] = frame.RY,
            ["lt"] = frame.LT, ["rt"] = frame.RT,
            ["buttons"] = buttons,
            ["raw"] = raw,
            ["hasPov"] = frame.HasPov,
            ["names"] = names,
            ["defaults"] = JsonSerializer.SerializeToNode(PadDb.Defaults(row.Token)),
            ["targets"] = new JsonArray(Targets.Select(n => (JsonNode)n!).ToArray()),
            ["map"] = JsonSerializer.SerializeToNode(Merge(PadDb.Defaults(row.Token), row.Map))
        };
    }

    JsonObject TestVib(JsonObject req)
    {
        var token = req["token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token))
        {
            var slot = req["slot"]?.GetValue<int>() ?? -1;
            if (slot < 0 || slot >= _slots.Count || string.IsNullOrEmpty(_slots[slot].Physical))
                return Err("No pad on that slot");
            token = _slots[slot].Physical;
        }
        var pad = _pads.FirstOrDefault(p => p.Token == token);
        if (pad == null) return Err("That pad is not connected");
        if (!PadReader.CanVibrate(token))
            return Err(pad.Name + " does not support vibration");

        int level = Math.Clamp(req["level"]?.GetValue<int>() ?? 3, 1, 3);
        bool infinite = req["infinite"]?.GetValue<bool>() ?? false;
        int ms = Math.Clamp(req["ms"]?.GetValue<int>() ?? 600, 100, 6000);

        var t = token;
        if (infinite)
        {
            bool on = PadReader.TestVibrateLevel(t, level, 0);
            var ist = State();
            ist["message"] = PadReader.IsHolding(t)
                ? "Endless vibration on " + pad.Name + " — hit it again to stop"
                : "Endless vibration stopped";
            ist["hold"] = PadReader.IsHolding(t);
            return Ok(ist);
        }

        // Run off the UI thread so the bridge stays responsive during the buzz.
        System.Threading.Tasks.Task.Run(() => PadReader.TestVibrateLevel(t, level, ms));

        var st = State();
        st["message"] = "Test vibration sent to " + pad.Name;
        return Ok(st);
    }

    JsonObject Logs(JsonObject req)
    {
        var app = new JsonArray();
        foreach (var l in AppLog.Instance.Recent(500)) app.Add(l);

        var hook = new JsonArray();
        var dir = _gamePath != null ? Path.GetDirectoryName(_gamePath) : null;
        if (dir != null)
        {
            var file = Path.Combine(dir, "mapivo-hook.log");
            try { if (File.Exists(file)) foreach (var l in TailLines(file, 400)) hook.Add(l); }
            catch (Exception ex) { hook.Add("could not read mapivo-hook.log: " + ex.Message); }
        }
        return new JsonObject { ["ok"] = true, ["lines"] = app, ["hookLines"] = hook };
    }

    /* ---------- virtual drivers ---------- */

    void DriverSeed()
    {
        _settings.DriverSlots ??= new List<AppSettings.DriverSlot>();
        foreach (var d in _settings.DriverSlots)
            VirtualPadManager.SetSource(d.Index, d.Token);
    }

    string NextFreeDriverIndex()
    {
        var used = new HashSet<int>();
        foreach (var d in _settings.DriverSlots) used.Add(d.Index);
        for (int i = 0; i < VirtualPadManager.MaxSlots; i++)
            if (!used.Contains(i)) return i.ToString();
        return "";
    }

    JsonObject DriverAdd()
    {
        if (_settings.DriverSlots.Count >= VirtualPadManager.MaxSlots)
            return Err("All 8 controllers are already added");
        var idx = NextFreeDriverIndex();
        if (idx.Length == 0) return Err("All 8 controllers are already added");
        _settings.DriverSlots.Add(new AppSettings.DriverSlot
        {
            Index = int.Parse(idx),
            Token = "",
            Enabled = true
        });
        _settings.Save();
        var st = DriverStatus();
        st["message"] = "Controller added";
        return st;
    }

    JsonObject DriverRemove(JsonObject req)
    {
        int index = req["index"]?.GetValue<int>() ?? -1;
        if (index >= 0 && index < VirtualPadManager.MaxSlots)
        {
            if (_driverRunning) return Err("Turn the driver off before removing a controller");
            _settings.DriverSlots.RemoveAll(d => d.Index == index);
            VirtualPadManager.SetSource(index, "");
            _settings.Save();
        }
        var st = DriverStatus();
        st["message"] = "Controller removed";
        return st;
    }

    JsonObject DriverSource(JsonObject req)
    {
        int index = req["index"]?.GetValue<int>() ?? -1;
        if (index < 0 || index >= VirtualPadManager.MaxSlots) return Err("Bad controller");
        var token = req["token"]?.GetValue<string>() ?? "";
        // One physical pad may only feed one virtual controller.
        if (token.Length > 0 && _settings.DriverSlots.Any(d => d.Index != index && d.Token == token))
            return Err("That pad already drives another virtual controller");
        var row = _settings.DriverSlots.FirstOrDefault(d => d.Index == index);
        if (row == null) _settings.DriverSlots.Add(new AppSettings.DriverSlot { Index = index, Token = token });
        else row.Token = token;
        VirtualPadManager.SetSource(index, token);
        _settings.Save();
        var st = DriverStatus();
        st["message"] = "Controller saved";
        return st;
    }

    JsonObject DriverToggle(JsonObject req)
    {
        int index = req["index"]?.GetValue<int>() ?? -1;
        bool on = req["on"]?.GetValue<bool>() ?? false;
        var row = _settings.DriverSlots.FirstOrDefault(d => d.Index == index);
        if (row == null) return Err("Bad controller");
        row.Enabled = on;
        _settings.Save();
        return DriverStatus();
    }

    JsonObject DriverStart()
    {
        if (_driverRunning) return DriverStatus();
        var routes = new List<DriverEngine.Route>();
        foreach (var row in _settings.DriverSlots.Where(d => d.Enabled))
        {
            var map = new Dictionary<string, string>(PadDb.Defaults(row.Token));
            foreach (var kv in row.Map) map[kv.Key] = kv.Value;
            routes.Add(new DriverEngine.Route { Index = row.Index, Token = row.Token, Map = map });
        }

        if (routes.Count == 0) return Err("No controller is turned on");

        if (!_driver.Start(routes))
        {
            var bad = Err(_driver.Error.Length > 0 ? _driver.Error : "Could not start the driver");
            bad["driver"] = DriverNode();
            return bad;
        }
        _driverRunning = true;
        var st = DriverStatus();
        st["message"] = routes.Count + " virtual controller(s) are live";
        return st;
    }

    JsonObject DriverStop()
    {
        _driver.Stop();
        _driverRunning = false;
        var st = DriverStatus();
        st["message"] = "Virtual controllers stopped";
        return st;
    }

    JsonObject DriverNode()
    {
        var rows = new JsonArray();
        foreach (var row in _settings.DriverSlots.OrderBy(d => d.Index))
        {
            var pad = _pads.FirstOrDefault(p => p.Token == row.Token);
            rows.Add(new JsonObject
            {
                ["index"] = row.Index,
                ["token"] = row.Token,
                ["enabled"] = row.Enabled,
                ["name"] = pad?.Name ?? (string.IsNullOrEmpty(row.Token) ? "" : row.Token),
                ["connected"] = pad != null,
                ["source"] = pad == null ? "" : (pad.Source == PadSource.XInput ? "xinput" : "di"),
                ["map"] = new JsonObject(row.Map.ToDictionary(kv => kv.Key, kv => (JsonNode?)kv.Value))
            });
        }

        var pads = new JsonArray();
        foreach (var p in _pads)
            pads.Add(new JsonObject
            {
                ["token"] = p.Token,
                ["name"] = p.Name,
                ["source"] = p.Source == PadSource.XInput ? "xinput" : "di",
                ["usedBy"] = _settings.DriverSlots.Any(d => d.Token == p.Token)
                    ? _settings.DriverSlots.First(d => d.Token == p.Token).Index
                    : -1
            });

        return new JsonObject
        {
            ["mode"] = "vigem",
            ["rows"] = rows,
            ["pads"] = pads,
            ["running"] = _driverRunning,
            ["live"] = VirtualPadManager.List().Count(v => v.Attached),
            ["vigemInstalled"] = VirtualPadManager.DriverPath().Length > 0,
            ["vigemPath"] = VirtualPadManager.DriverPath(),
            ["problem"] = VirtualPadManager.DriverPath().Length == 0
                ? "ViGEmBus is not installed"
                : "",
            ["max"] = VirtualPadManager.MaxSlots,
            ["dllRoot"] = _settings.DllFolderFull,
            ["x86Count"] = CountDlls("x86"),
            ["x64Count"] = CountDlls("x64"),
            ["arch"] = _arch.ToString(),
            ["game"] = _gamePath == null ? "" : Path.GetFileName(_gamePath),
            ["installedInGame"] = _gamePath != null
                && File.Exists(Path.Combine(Path.GetDirectoryName(_gamePath)!, "xinput1_3.dll"))
        };
    }

    int CountDlls(string sub)
    {
        try
        {
            var p = Path.Combine(_settings.DllFolderFull, sub);
            return Directory.Exists(p) ? Directory.GetFiles(p, "*.dll").Length : 0;
        }
        catch { return 0; }
    }

    JsonObject DriverStatus()
    {
        return new JsonObject
        {
            ["ok"] = true,
            ["state"] = State(),
            ["driver"] = DriverNode()
        };
    }

    JsonObject OpenDllFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.DllFolderFull);
            Process.Start("explorer.exe", _settings.DllFolderFull);
        }
        catch (Exception ex)
        {
            return Err(ex.Message);
        }
        return DriverStatus();
    }

    static List<string> TailLines(string file, int max)
    {
        var list = new List<string>();
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long start = Math.Max(0, fs.Length - 256 * 1024);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        string? line;
        while ((line = sr.ReadLine()) != null)
            if (line.Trim().Length > 0) list.Add(line.TrimEnd());
        if (start > 0 && list.Count > 0) list.RemoveAt(0);
        if (list.Count > max) list.RemoveRange(0, list.Count - max);
        return list;
    }

    // Axis sources usable as mapping inputs: analog triggers and the sign of
    // each stick axis, resolved by the hook the same way.
    static readonly string[] AxisSources =
    {
        "lt", "rt", "lx+", "lx-", "ly+", "ly-", "rx+", "rx-", "ry+", "ry-"
    };

    JsonObject SaveMap(JsonObject req)
    {
        var slot = req["slot"]?.GetValue<int>() ?? -1;
        if (slot < 0 || slot >= 4) return Err("Bad slot");
        if (_profile == null) return Err("Select a game first");

        var map = new Dictionary<string, string>();
        if (req["map"] is JsonObject mo)
            foreach (var kv in mo)
            {
                var v = kv.Value?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(v) && v != kv.Key) map[kv.Key] = v!;
            }
        _slots[slot].Map = map;
        ApplySlotsTo(_profile);
        ProfileStore.Save(_profile);

        var st = State();
        st["message"] = "Mapping saved for " + _profile.DisplayName;
        return Ok(st);
    }

    /* ---------- helpers ---------- */

    void LoadSlotsFrom(GameProfile p)
    {
        _slots = Enumerable.Range(0, 4).Select(i =>
        {
            var b = p.Bindings.FirstOrDefault(x => x.VirtualSlot == i);
            var s = new SlotState
            {
                Physical = b?.Physical ?? "",
                Vib = b?.VibrationEnabled ?? true,
                Map = b?.Map != null ? new Dictionary<string, string>(b.Map) : new()
            };
            foreach (var kv in PadDb.Defaults(s.Physical))
                if (!s.Map.ContainsKey(kv.Key)) s.Map[kv.Key] = kv.Value;
            Sanitize(s);
            return s;
        }).ToList();
    }

    // An XInput pad reports canonical buttons, so a raw "bN" entry from an
    // older profile would make two virtual controls read one physical button
    // (Back and Start did). Drop those entries.
    static void Sanitize(SlotState s)
    {
        if (s.Physical.Length == 0 || (s.Physical[0] != 'x' && s.Physical[0] != 'X')) return;
        foreach (var k in s.Map.Where(kv => kv.Value.Length > 1 && kv.Value[0] == 'b')
                               .Select(kv => kv.Key).ToList())
            s.Map.Remove(k);
    }

    void ReadSlots(JsonObject req)
    {
        if (req["slots"] is not JsonArray arr) return;
        var next = new List<SlotState>();
        for (int i = 0; i < 4; i++)
        {
            var s = _slots[i];
            if (i < arr.Count && arr[i] is JsonObject o)
            {
                s.Physical = o["physical"]?.GetValue<string>() ?? s.Physical;
                s.Vib = o["vib"]?.GetValue<bool>() ?? s.Vib;
                if (o["map"] is JsonObject mo)
                    s.Map = mo.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? "");
                Sanitize(s);
            }
            next.Add(s);
        }
        _slots = next;
    }

    // Device defaults first, the user's saved entries win. The UI and the
    // driver must see the same map the hook will resolve.
    static Dictionary<string, string> Merge(Dictionary<string, string> defaults, Dictionary<string, string> overrides)
    {
        var map = new Dictionary<string, string>(defaults);
        foreach (var kv in overrides)
            if (!string.IsNullOrEmpty(kv.Value)) map[kv.Key] = kv.Value;
        return map;
    }

    void ApplySlotsTo(GameProfile p)
    {
        p.Bindings.Clear();
        for (int i = 0; i < 4; i++)
        {
            var s = _slots[i];
            var pad = _pads.FirstOrDefault(x => x.Token == s.Physical);
            var prev = p.Bindings.FirstOrDefault(x => x.VirtualSlot == i);
            p.Bindings.Add(new ControllerBinding
            {
                VirtualSlot = i,
                Physical = s.Physical,
                Name = pad?.Name ?? prev?.Name ?? "",
                VibrationEnabled = s.Vib,
                Map = new Dictionary<string, string>(s.Map)
            });
        }
    }

    JsonObject State()
    {
        var pads = new JsonArray();
        foreach (var p in _pads)
            pads.Add(new JsonObject
            {
                ["index"] = p.Index,
                ["name"] = p.Name,
                ["source"] = p.Source == PadSource.XInput ? "xinput" : "di",
                ["token"] = p.Token
            });

        var slots = new JsonArray();
        for (int i = 0; i < _slots.Count; i++)
        {
            var s = _slots[i];
            var map = new JsonObject();
            foreach (var kv in Merge(PadDb.Defaults(s.Physical), s.Map)) map[kv.Key] = kv.Value;
            var pad = _pads.FirstOrDefault(x => x.Token == s.Physical);
            var saved = _profile?.Bindings.FirstOrDefault(b => b.VirtualSlot == i)?.Name ?? "";
            slots.Add(new JsonObject
            {
                ["physical"] = s.Physical,
                ["vib"] = s.Vib,
                ["map"] = map,
                ["name"] = pad?.Name ?? saved,
                ["connected"] = pad != null,
                ["canVib"] = pad != null && PadReader.CanVibrate(pad.Token)
            });
        }

        var st = new JsonObject
        {
            ["pads"] = pads,
            ["slots"] = slots,
            ["arch"] = _arch.ToString().ToLowerInvariant(),
            ["dll"] = _dll != null ? Path.GetFileName(Path.GetDirectoryName(_dll)) + "/" + Path.GetFileName(_dll) : null,
            // Always present so app.js overwrites stale values: after a profile
            // is deleted the dashboard must fall back to "no profile" instead
            // of keeping the game it was showing a moment ago.
            ["game"] = null,
            ["profile"] = null,
            ["buttonNames"] = new JsonArray(),
            ["mappingTargets"] = new JsonArray(),
            ["axisSources"] = new JsonArray(AxisSources.Select(n => (JsonNode)n!).ToArray())
        };

        if (_profile != null)
        {
            var btns = new JsonArray();
            foreach (var n in PadReader.ButtonNames) btns.Add(n);
            st["buttonNames"] = btns;
            st["mappingTargets"] = new JsonArray(Targets.Select(n => (JsonNode)n!).ToArray());
            st["game"] = new JsonObject
            {
                ["name"] = _profile.DisplayName,
                ["path"] = _gamePath ?? ""
            };
            st["profile"] = new JsonObject
            {
                ["displayName"] = _profile.DisplayName,
                ["executable"] = _profile.Executable,
                ["gamePath"] = _profile.GamePath,
                ["enabled"] = _profile.Enabled,
                ["leftDeadzone"] = _profile.Bindings.FirstOrDefault()?.LeftDeadzone ?? 0.08f,
                ["rightDeadzone"] = _profile.Bindings.FirstOrDefault()?.RightDeadzone ?? 0.08f
            };
        }
        return st;
    }

    JsonArray ProfilesNode()
    {
        var arr = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ProfileStore.All())
        {
            if (!string.IsNullOrEmpty(p.GamePath)) seen.Add(p.GamePath);
            arr.Add(new JsonObject
            {
                ["displayName"] = p.DisplayName,
                ["executable"] = p.Executable,
                ["gamePath"] = p.GamePath,
                ["exists"] = !string.IsNullOrEmpty(p.GamePath) && File.Exists(p.GamePath),
                ["hasProfile"] = true
            });
        }
        foreach (var g in _settings.KnownGames)
        {
            if (seen.Contains(g) || !File.Exists(g)) continue;
            arr.Add(new JsonObject
            {
                ["displayName"] = Path.GetFileNameWithoutExtension(g),
                ["executable"] = Path.GetFileName(g),
                ["gamePath"] = g,
                ["exists"] = true,
                ["hasProfile"] = false
            });
        }
        return arr;
    }

    JsonObject SettingsNode() => new()
    {
        ["autoLaunchGame"] = _settings.AutoLaunchGame,
        ["startMinimized"] = _settings.StartMinimized,
        ["dllFolder"] = _settings.DllFolder,
        ["language"] = _settings.Language,
        ["version"] = AppInfo.Version
    };

    // A message belongs at the top level: app.js toasts res.message, and it
    // would otherwise be buried inside the state object.
    static JsonObject Ok(JsonObject state)
    {
        var res = new JsonObject { ["ok"] = true, ["state"] = state };
        if (state["message"] is JsonNode m)
        {
            res["message"] = m.DeepClone();
            state.Remove("message");
        }
        return res;
    }
    static JsonObject Err(string msg) => new() { ["ok"] = false, ["message"] = msg };
}

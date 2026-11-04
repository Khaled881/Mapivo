using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Mapivo.Core;

namespace Mapivo.App;

public partial class MainWindow : Window
{
    readonly AppSettings _settings = AppSettings.Load();
    readonly WebBridge _bridge;
    readonly List<string> _logBacklog = new();
    HwndSource? _hwndSource;
    IntPtr _deviceNotify;
    DispatcherTimer? _hotplugTimer;

    public MainWindow()
    {
        InitializeComponent();
        _bridge = new WebBridge(_settings);
        PadReader.Window = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();

        AppLog.Instance.LineAdded += line => Dispatcher.Invoke(() => PushLog(line));

        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) =>
        {
            _hotplugTimer?.Stop();
            UnregisterDeviceNotifications();
            _bridge.Shutdown();
        };
    }

    void HookDeviceNotifications()
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            _hwndSource = HwndSource.FromHwnd(helper.Handle);
            _hwndSource?.AddHook(WndProc);

            var filter = new DEV_BROADCAST_DEVICEINTERFACE
            {
                dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE
            };
            _deviceNotify = RegisterDeviceNotification(
                helper.Handle, ref filter, DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Write("Device notifications unavailable: " + ex.Message);
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE &&
            (wParam.ToInt32() == DBT_DEVNODES_CHANGED || wParam.ToInt32() == DBT_DEVICEARRIVAL || wParam.ToInt32() == DBT_DEVICEREMOVECOMPLETE))
        {
            // Device stack settles a moment after the message; coalesce bursts.
            _hotplugTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _hotplugTimer.Stop();
            _hotplugTimer.Tick -= OnHotplugTick;
            _hotplugTimer.Tick += OnHotplugTick;
            _hotplugTimer.Start();
        }
        return IntPtr.Zero;
    }

    void OnHotplugTick(object? sender, EventArgs e)
    {
        _hotplugTimer?.Stop();
        try
        {
            var st = _bridge.RescanIfChanged();
            if (st != null) PushState(st.ToJsonString());
        }
        catch (Exception ex)
        {
            AppLog.Instance.Write("Hotplug rescan failed: " + ex.Message);
        }
    }

    void PushState(string json)
    {
        if (Web.CoreWebView2 == null) return;
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            $"window.mapivoState && window.mapivoState({JsonSerializer.Serialize(json)});");
    }

    void UnregisterDeviceNotifications()
    {
        if (_deviceNotify != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_deviceNotify);
            _deviceNotify = IntPtr.Zero;
        }
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    const int WM_DEVICECHANGE = 0x0219;
    const int DBT_DEVICEARRIVAL = 0x8000;
    const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    const int DBT_DEVNODES_CHANGED = 0x0007;
    const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    [StructLayout(LayoutKind.Sequential)]
    struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public char[] dbcc_name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, ref DEV_BROADCAST_DEVICEINTERFACE notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterDeviceNotification(IntPtr handle);

    void PushLog(string line)
    {
        if (_logBacklog.Count >= 2000) _logBacklog.RemoveRange(0, 1000);
        _logBacklog.Add(line);
        if (Web.CoreWebView2 == null) return;
        _ = Web.CoreWebView2.ExecuteScriptAsync(
            $"window.mapivoLog && window.mapivoLog({JsonSerializer.Serialize(line)});");
    }

    async Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Mapivo", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            HookDeviceNotifications();

            var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
            core.SetVirtualHostNameToFolderMapping(
                "mapivo.local", webRoot, CoreWebView2HostResourceAccessKind.Allow);

            Web.Source = new Uri("https://mapivo.local/index.html");
            AppLog.Instance.Write("Mapivo started");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not start the UI renderer (WebView2).\n\n" + ex.Message,
                "Mapivo", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string result;
        string id = "";
        try
        {
            var req = JsonNode.Parse(e.TryGetWebMessageAsString() ?? "")?.AsObject() ?? new JsonObject();
            var cmd = req["cmd"]?.GetValue<string>() ?? "";
            // Messages without a cmd are not requests (e.g. WebView2 internals).
            if (cmd.Length == 0) return;
            id = req["id"]?.ToString() ?? "";
            result = cmd switch
            {
                "load" => _bridge.Load(),
                "act" => _bridge.Act(req["payload"]?.ToJsonString() ?? "{}"),
                _ => "{\"ok\":false,\"message\":\"Unknown command\"}"
            };
        }
        catch (Exception ex)
        {
            AppLog.Instance.Write("Bridge error: " + ex.Message);
            result = JsonSerializer.Serialize(new { ok = false, message = ex.Message });
        }
        // Echo the request id so the UI can pair this reply with its caller;
        // several requests (live polling + user actions) are in flight at once.
        Web.CoreWebView2?.PostWebMessageAsString(
            JsonSerializer.Serialize(new { id, result }));
    }
}

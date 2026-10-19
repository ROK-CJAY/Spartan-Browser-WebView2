using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SpartanBrowser;

sealed class ShellForm : Form
{
    readonly WebView2 _shell = new() { Dock = DockStyle.Fill };
    readonly Dictionary<string, WebView2> _guests = new();
    readonly Dictionary<string, string> _lastNav = new();
    readonly Dictionary<string, int> _reload = new();
    CoreWebView2Environment? _env;
    Process? _server;
    bool _kiosk;
    FormBorderStyle _border = FormBorderStyle.Sizable;
    string _updatePhase = "idle";
    string? _latest;
    string? _installer;
    int _percent;

    public ShellForm()
    {
        Text = "Spartan Browser";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(1024, 700);
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;
        Controls.Add(_shell);
        Load += async (_, _) => await BootAsync();
        FormClosing += (_, _) =>
        {
            try { if (_server is { HasExited: false }) _server.Kill(true); } catch { /* already gone */ }
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11) SetKiosk(!_kiosk);
            if (e.KeyCode == Keys.Escape && _kiosk) SetKiosk(false);
        };
    }

    async Task BootAsync()
    {
        var root = AppContext.BaseDirectory;
        var entry = Native.ServerEntry(root);
        if (entry is not null) _server = Native.StartServer(entry);
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpartanBrowser", "WebView2");
        Directory.CreateDirectory(userData);
        _env = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = Native.BrowserArgs,
        });
        await _shell.EnsureCoreWebView2Async(_env);
        var core = _shell.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript());
        core.WebMessageReceived += (_, e) => OnShellMessage(e.WebMessageAsJson);
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Post(new { type = "open-tab", url = e.Uri });
        };
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith($"http://127.0.0.1:{Native.Port}", StringComparison.OrdinalIgnoreCase)) return;
            if (e.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                e.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                Post(new { type = "open-tab", url = e.Uri });
            }
        };
        _shell.CoreWebView2.Navigate($"http://127.0.0.1:{Native.Port}/");
    }

    void OnShellMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
        if (type == "slot")
        {
            var id = root.GetProperty("tabId").GetString() ?? "";
            var url = root.GetProperty("url").GetString() ?? "";
            var reload = root.TryGetProperty("reloadKey", out var rk) ? rk.GetInt32() : 0;
            var visible = root.TryGetProperty("visible", out var vis) && vis.GetBoolean();
            var bounds = new Rectangle(
                root.GetProperty("x").GetInt32(),
                root.GetProperty("y").GetInt32(),
                root.GetProperty("w").GetInt32(),
                root.GetProperty("h").GetInt32());
            _ = EnsureGuestAsync(id, url, reload, visible, bounds);
            return;
        }
        if (type == "slot-close")
        {
            var id = root.GetProperty("tabId").GetString() ?? "";
            if (_guests.Remove(id, out var guest))
            {
                Controls.Remove(guest);
                guest.Dispose();
            }
            return;
        }
        if (type == "rpc")
        {
            var id = root.GetProperty("id").GetString() ?? "";
            var method = root.GetProperty("method").GetString() ?? "";
            _ = ReplyRpcAsync(id, method, root);
        }
    }

    async Task EnsureGuestAsync(string id, string url, int reload, bool visible, Rectangle bounds)
    {
        if (_env is null || id.Length == 0) return;
        if (!_guests.TryGetValue(id, out var guest))
        {
            guest = new WebView2 { Visible = false };
            _guests[id] = guest;
            Controls.Add(guest);
            await guest.EnsureCoreWebView2Async(_env);
            var core = guest.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(LoginCaptureScript());
            var tabId = id;
            core.WebMessageReceived += (_, e) => OnGuestLogin(tabId, e.WebMessageAsJson);
            core.NavigationCompleted += (_, _) =>
                Post(new { type = "guest-nav", tabId, url = core.Source });
            core.DocumentTitleChanged += (_, _) =>
                Post(new { type = "guest-title", tabId, title = core.DocumentTitle });
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Post(new { type = "open-tab", url = e.Uri });
            };
            core.Navigate(url);
            _reload[id] = reload;
            _lastNav[id] = url;
        }
        else if (_reload.GetValueOrDefault(id) != reload)
        {
            _reload[id] = reload;
            _lastNav[id] = url;
            guest.CoreWebView2?.Reload();
        }
        else if (!string.Equals(_lastNav.GetValueOrDefault(id), url, StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(url))
        {
            _lastNav[id] = url;
            guest.CoreWebView2?.Navigate(url);
        }
        guest.Bounds = bounds;
        guest.Visible = visible;
        if (visible) guest.BringToFront();
    }

    async Task ReplyRpcAsync(string id, string method, JsonElement root)
    {
        object? result = null;
        try
        {
            result = method switch
            {
                "version" => Native.Version,
                "windows-identity" => await Native.WindowsIdentityAsync(),
                "launch" => Native.LaunchApp(Arg(root, "id")),
                "elevated" => Native.LaunchElevated(Arg(root, "command"), Arg(root, "password")),
                "update-get" => UpdateSnapshot(),
                "update-check" => await CheckUpdateAsync(),
                "update-install" => InstallUpdate(),
                "kiosk" => ToggleKiosk(root),
                _ => new { error = "Unknown method." },
            };
        }
        catch (Exception ex)
        {
            result = new { error = ex.Message };
        }
        Post(new { type = "rpc", id, result });
    }

    static string Arg(JsonElement root, string name)
    {
        if (!root.TryGetProperty("args", out var args)) return "";
        return args.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
    }

    object ToggleKiosk(JsonElement root)
    {
        var on = root.TryGetProperty("args", out var args) &&
                 args.TryGetProperty("on", out var flag) && flag.GetBoolean();
        SetKiosk(on);
        return new { ok = true, kiosk = _kiosk };
    }

    void SetKiosk(bool on)
    {
        _kiosk = on;
        if (on)
        {
            _border = FormBorderStyle;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            TopMost = true;
        }
        else
        {
            TopMost = false;
            FormBorderStyle = _border;
            WindowState = FormWindowState.Maximized;
        }
    }

    object UpdateSnapshot() => new
    {
        phase = _updatePhase,
        currentVersion = Native.Version,
        latestVersion = _latest,
        percent = _percent,
        error = (string?)null,
        packaged = true,
    };

    async Task<object> CheckUpdateAsync()
    {
        _updatePhase = "checking";
        Post(new { type = "update", status = UpdateSnapshot() });
        try
        {
            var release = await Native.LatestReleaseAsync();
            var tag = release?.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            _latest = tag;
            if (string.IsNullOrWhiteSpace(tag) || tag == Native.Version)
            {
                _updatePhase = "current";
                return UpdateSnapshot();
            }
            string? asset = null;
            if (release is { } doc && doc.TryGetProperty("assets", out var assets))
            {
                foreach (var item in assets.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        asset = item.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }
            if (asset is null)
            {
                _updatePhase = "available";
                return UpdateSnapshot();
            }
            _updatePhase = "downloading";
            Post(new { type = "update", status = UpdateSnapshot() });
            var dest = Path.Combine(Path.GetTempPath(), "SpartanBrowser-update" + Path.GetExtension(asset));
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(asset);
            await File.WriteAllBytesAsync(dest, bytes);
            _installer = dest;
            _percent = 100;
            _updatePhase = "ready";
        }
        catch (Exception ex)
        {
            _updatePhase = "error";
            return new
            {
                phase = "error",
                currentVersion = Native.Version,
                latestVersion = _latest,
                percent = _percent,
                error = ex.Message,
                packaged = true,
            };
        }
        Post(new { type = "update", status = UpdateSnapshot() });
        return UpdateSnapshot();
    }

    object InstallUpdate()
    {
        if (_updatePhase != "ready" || string.IsNullOrWhiteSpace(_installer)) return new { ok = false };
        Process.Start(new ProcessStartInfo(_installer) { UseShellExecute = true });
        Close();
        return new { ok = true };
    }

    void OnGuestLogin(string tabId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
            if (type != "login-attempt") return;
            var username = root.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
            var password = root.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
            var url = root.TryGetProperty("url", out var href) ? href.GetString() ?? "" : "";
            if (username.Length == 0 || password.Length == 0) return;
            Post(new { type = "login-offer", tabId, url, username, password });
        }
        catch
        {
            /* ignore malformed page messages */
        }
    }

    static string LoginCaptureScript() => """
        (() => {
          if (window.__spartanLoginCapture) return;
          window.__spartanLoginCapture = true;
          function grab(form) {
            const pass = form.querySelector('input[type="password"]');
            if (!pass || !pass.value) return;
            const user = form.querySelector('input[type="email"], input[type="text"], input[name*="user" i], input[autocomplete="username"]');
            if (!user || !user.value) return;
            window.chrome.webview.postMessage({
              type: "login-attempt",
              username: user.value,
              password: pass.value,
              url: location.href
            });
          }
          document.addEventListener("submit", (event) => {
            const form = event.target;
            if (form && form.tagName === "FORM") grab(form);
          }, true);
        })();
        """;

    void Post(object payload)
    {
        if (_shell.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(payload);
        try { _shell.CoreWebView2.PostWebMessageAsJson(json); } catch { /* page not ready */ }
    }

    static string BridgeScript() => """
        (() => {
          const pending = new Map();
          const updateListeners = new Set();
          const tabListeners = new Set();
          let seq = 0;
          function rpc(method, args) {
            const id = String(++seq);
            return new Promise((resolve) => {
              pending.set(id, resolve);
              window.chrome.webview.postMessage({ type: "rpc", id, method, args: args || {} });
            });
          }
          window.chrome.webview.addEventListener("message", (event) => {
            const data = event.data || {};
            if (data.type === "rpc" && pending.has(data.id)) {
              const done = pending.get(data.id);
              pending.delete(data.id);
              done(data.result);
            }
            if (data.type === "update") updateListeners.forEach((fn) => fn(data.status));
            if (data.type === "open-tab" && data.url) tabListeners.forEach((fn) => fn(data.url));
          });
          window.spartanDesktop = {
            packaged: true,
            host: "webview2",
            version: () => rpc("version"),
            windowsIdentity: () => rpc("windows-identity"),
            launchApp: (id) => rpc("launch", { id }),
            launchElevated: (payload) => rpc("elevated", payload),
            getUpdateStatus: () => rpc("update-get"),
            checkForUpdates: () => rpc("update-check"),
            installUpdate: () => rpc("update-install"),
            onUpdateStatus: (callback) => {
              updateListeners.add(callback);
              return () => updateListeners.delete(callback);
            },
            onOpenTab: (callback) => {
              tabListeners.add(callback);
              return () => tabListeners.delete(callback);
            },
          };
        })();
        """;
}

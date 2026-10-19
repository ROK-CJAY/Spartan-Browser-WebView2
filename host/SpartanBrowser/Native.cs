using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpartanBrowser;

static class Native
{
    public const string Version = "0.10.0";
    public const string Repo = "ROK-CJAY/Spartan-Browser-WebView2";
    public const int Port = 47821;

    public static readonly string AuthList = string.Join(",",
        "*miamidade.gov", "*.miamidade.gov", "nsd.miamidade.gov", "smartit.miamidade.gov",
        "myit.miamidade.gov", "*onbmc.com", "*.onbmc.com", "*sharepoint.com", "*.sharepoint.com",
        "login.microsoftonline.com", "*.microsoftonline.com", "login.windows.net", "login.microsoft.com",
        "autologon.microsoftazuread-sso.com", "*.microsoftazuread-sso.com", "*.msftauth.net",
        "*.msauth.net", "*.windows.net", "*.office.com", "*.office365.com");

    public static string BrowserArgs =>
        $"--auth-server-whitelist={AuthList} --auth-server-allowlist={AuthList} " +
        $"--auth-negotiate-delegate-whitelist={AuthList} --auth-negotiate-delegate-allowlist={AuthList} " +
        "--auth-schemes=ntlm,negotiate --proxy-auto-detect " +
        "--enable-features=CloudAPAuth,msSingleSignOnOSForPrimaryAccountIsShared " +
        "--disable-features=ThirdPartyStoragePartitioning,TrackingProtection3pcd";

    public static async Task<object?> WindowsIdentityAsync()
    {
        try
        {
            var raw = await RunAsync("powershell.exe",
                "-NoProfile -Command \"$u=(whoami /upn); $a=(whoami); $d=''; try { Add-Type -AssemblyName System.DirectoryServices.AccountManagement; $d=[System.DirectoryServices.AccountManagement.UserPrincipal]::Current.DisplayName } catch {}; if (-not $d) { try { $d=([adsi]('WinNT://'+$env:USERDOMAIN+'/'+$env:USERNAME+',user')).FullName } catch {} }; Write-Output ('UPN='+$u); Write-Output ('ACCOUNT='+$a); Write-Output ('DISPLAY='+$d)\"");
            var upn = Line(raw, "UPN");
            var account = Line(raw, "ACCOUNT");
            var displayName = Line(raw, "DISPLAY");
            if (string.IsNullOrWhiteSpace(upn) || !upn.Contains('@')) return null;
            return new { upn, account, displayName };
        }
        catch
        {
            return null;
        }
    }

    static string Line(string raw, string key)
    {
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                return trimmed[(key.Length + 1)..].Trim();
        }
        return "";
    }

    static readonly Dictionary<string, (string? path, string cmd, string[] args)[]> Launchers = new()
    {
        ["notepad"] = [(null, "notepad.exe", [])],
        ["calc"] = [(null, "calc.exe", [])],
        ["teamviewer"] = [
            (@"C:\Program Files\TeamViewer\TeamViewer.exe", "", []),
            (@"C:\Program Files (x86)\TeamViewer\TeamViewer.exe", "", []),
            (null, "TeamViewer.exe", []),
        ],
        ["mainframe"] = [
            (@"C:\Program Files\MochaSoft\Mocha TN3270\tn3270.exe", "", []),
            (@"C:\Program Files (x86)\MochaSoft\Mocha TN3270\tn3270.exe", "", []),
            (null, "tn3270.exe", []),
        ],
        ["lockout"] = [
            (@"C:\Program Files\Windows Resource Kits\Tools\lockoutstatus.exe", "", []),
            (@"C:\Program Files (x86)\Windows Resource Kits\Tools\lockoutstatus.exe", "", []),
            (null, "lockoutstatus.exe", []),
        ],
        ["aduc"] = [(null, "mmc.exe", ["dsa.msc"])],
        ["cmrc"] = [
            (@"C:\Program Files (x86)\Microsoft Configuration Manager\AdminConsole\bin\i386\CmRcViewer.exe", "", []),
            (@"C:\Program Files\Microsoft Configuration Manager\AdminConsole\bin\i386\CmRcViewer.exe", "", []),
            (null, "CmRcViewer.exe", []),
        ],
    };

    public static object LaunchApp(string id)
    {
        if (!Launchers.TryGetValue((id ?? "").ToLowerInvariant(), out var items))
            return new { ok = false, error = "Unknown desktop app." };
        foreach (var item in items)
        {
            try
            {
                if (item.path is not null)
                {
                    if (!File.Exists(item.path)) continue;
                    Process.Start(new ProcessStartInfo(item.path) { UseShellExecute = true });
                    return new { ok = true };
                }
                Process.Start(new ProcessStartInfo(item.cmd, item.args) { UseShellExecute = true });
                return new { ok = true };
            }
            catch
            {
                /* try the next candidate */
            }
        }
        return new { ok = false, error = "Could not open this tool. Install it on this PC." };
    }

    static readonly Regex Elevated =
        new(@"^powershell\.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand [A-Za-z0-9+/=]+$", RegexOptions.IgnoreCase);

    public static object LaunchElevated(string? command, string? password)
    {
        var cmd = (command ?? "").Trim();
        if (!Elevated.IsMatch(cmd)) return new { ok = false, error = "That launch command is not allowed." };
        var encoded = cmd.Split(' ').Last();
        var child = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
        });
        if (child is null) return new { ok = false, error = "Could not start that tool." };
        child.StandardInput.WriteLine(password ?? "");
        child.StandardInput.Close();
        return new { ok = true };
    }

    public static string? ServerEntry(string baseDir)
    {
        foreach (var rel in new[] { "app-server/server/index.mjs", "app-server/server/index.js" })
        {
            var path = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static Process? StartServer(string entry)
    {
        var node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        if (!File.Exists(node)) node = "node.exe";
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpartanBrowser", "pglite");
        Directory.CreateDirectory(data);
        var info = new ProcessStartInfo(node, $"\"{entry}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["PORT"] = Port.ToString();
        info.Environment["NITRO_PORT"] = Port.ToString();
        info.Environment["HOST"] = "127.0.0.1";
        info.Environment["NITRO_HOST"] = "127.0.0.1";
        info.Environment["PGLITE_DATA_DIR"] = data;
        return Process.Start(info);
    }

    public static async Task<JsonElement?> LatestReleaseAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpartanBrowser", Version));
        http.Timeout = TimeSpan.FromSeconds(20);
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    static async Task<string> RunAsync(string file, string args)
    {
        var proc = Process.Start(new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (proc is null) return "";
        var text = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return text;
    }
}

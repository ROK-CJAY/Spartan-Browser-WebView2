# Spartan Browser WebView2

Windows host for the Help Desk desk, built on Microsoft Edge WebView2.

The React desk is unchanged. This host starts the local desk server, opens it in WebView2, and provides Windows identity, desktop launchers, NTLM/negotiate for `*.miamidade.gov`, kiosk (F11), and a GitHub release updater.

## Build on a Windows PC

```
npm ci
npm run build
dotnet publish host/SpartanBrowser/SpartanBrowser.csproj -c Release -r win-x64 --self-contained true -o publish
```

Copy `.output` to `publish/app-server`, then run `publish/SpartanBrowser.exe`.

Requires the WebView2 Runtime (already on Windows 11) and Node.js on the PC so the desk server can start.

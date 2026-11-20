# Cellular Studio

A Windows app for sending and receiving SMS and running USSD codes through the laptop's built-in LTE modem
(Dell DW5820e / Fibocom L850-GL / Intel XMM7360). Windows' own Messaging app can only *show* messages
from your operator; this app can send SMS as well.

## Install

Run `dist\CellularStudio-<version>-x64.msi`.
- **Per-user install, no admin prompt**, into `%LOCALAPPDATA%\Programs\Cellular Studio`.
- Custom Setup page: **Start with Windows** (on by default; starts hidden in the tray) and **Desktop shortcut**
  (off by default). A Start-menu shortcut is always added.
- If the app is running, the installer closes it, including when it's hidden in the tray. Installing a newer MSI
  upgrades in place, and your startup setting is kept.
- Uninstall from *Settings → Apps*. This removes the app, the shortcuts and the sign-in entry (even one enabled from
  inside the app). Your messages and settings in `%LOCALAPPDATA%\CellularStudio` are kept.

To build the MSI: `installer\build-installer.ps1`. It publishes the app, regenerates the dialog artwork and runs WiX v5.
Bump `<Version>` in `Directory.Build.props` for each release you hand out.

## Run it (without installing)

`publish\CellularStudio.exe` is a self-contained single file. It needs no .NET install, so you can copy it anywhere.

- **Close (X)** hides the window to the **system tray**. The app keeps running and keeps receiving SMS.
- **Tray icon:** left-click opens the window. Right-click opens a menu with status, Check balance, New message, USSD,
  *Start with Windows*, and **Exit**. A colored dot on the icon shows the state: green means registered, amber means
  searching, red means no service, and blue means unread messages.
- **Start with Windows:** turn it on from the tray menu or from **Device → App**. It adds
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CellularStudio` = `"…\CellularStudio.exe" --tray`,
  so the app starts hidden with only the tray icon. If you move the exe, launch it once from the new location
  and the entry is updated automatically.
- **Single instance:** launching the exe again brings up the window that is already running.

## Pages

| Page | What it does |
|---|---|
| Overview | Carrier (logo or monogram badge), registration, and signal. Credit, data, and validity tiles are filled from the balance USSD reply. Quick actions. |
| Messages | Conversations with chat bubbles and a GSM-7/Unicode segment counter. Enter sends, Shift+Enter adds a new line. Retry failed messages. Service senders (alphanumeric IDs) are read-only. |
| USSD | Interactive sessions. Numbered menu options become one-click buttons. Quick codes come from the carrier (Hutch: `*344#` balance, `*123#` menu). Has a history list. Esc ends the session. |
| Device | Modem, SIM, and network details with copy buttons, a capabilities list (voice = no), the startup toggle, and an activity log. |

### Known limitations (Hutch LTE + DW5820e)
- **USSD doesn't work on LTE.** The modem is attached for data only, so USSD fails and briefly drops it off the network.
  It's blocked by default; the USSD page has a "Try anyway" switch, and the app reconnects automatically afterwards.
- **If the modem gets stuck searching**, click **Reconnect** on the banner. It asks Windows to re-register the modem
  (`IMbnRegistration::SetRegisterMode`). The app also does this automatically after 2 minutes.
- An SMS that fails instantly with no network cause usually means there isn't enough credit; Hutch sends a flash SMS saying so.
- **Balance comes from SMS, not USSD.** *Check balance* sends `BAL` to 344 (free) and reads Hutch's reply, which usually
  arrives within a few seconds. Reload notices from Hutch also update the credit. A balance older than 6 h is refreshed
  automatically at startup.
- **Signal:** only RSSI, signal level and an error-rate class are available. This driver doesn't report RSRP/RSRQ/SINR,
  cell IDs or neighbouring towers.

### Carrier logo
The app ships with brand-colored monogram badges. To show a real logo, drop a PNG named after the MCC+MNC
(e.g. `41308.png`) or the carrier name (`hutch.png`) into `%LOCALAPPDATA%\CellularStudio\logos\`
(or `Assets\Carriers\` next to the exe when building from source).

## Data
Messages, USSD history, and the last balance are stored in `%LOCALAPPDATA%\CellularStudio\data.json`.

## Build from source

Requires the .NET 10 SDK (installed per-user at `%LOCALAPPDATA%\Microsoft\dotnet`).

```powershell
$dn = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dn build CellularStudio.slnx
& $dn publish src\CellularStudio -c Release -o publish --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

| Project | Purpose |
|---|---|
| `src/Cellular.Core` | Modem status (WinRT + netsh), SMS (`Windows.Devices.Sms`), USSD (`UssdSession`), vendor AT tunnel, balance parser, storage |
| `src/CellularStudio` | WPF app (MVVM via CommunityToolkit.Mvvm), tray icon (WinForms `NotifyIcon`) |
| `src/Cellular.Cli` | Diagnostic CLI `cellular status \| caps \| at \| sms-list \| sms-send \| ussd` (`publish\cli\cellular.exe`) |
| `tools/` | Icon generator, plus screenshot and UI-automation helpers used during development |

See `../CLAUDE.md` for the hardware findings, including why voice calls and general AT commands are not possible on this setup.

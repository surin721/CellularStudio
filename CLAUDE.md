# Fibocom LTE modem — findings log

Running log of what has been learned about the WWAN modem in this laptop and the
SMS/USSD app in `app/`. Newest findings are added at the bottom of each section.

## Hardware / identity

| Item | Value |
|---|---|
| Laptop | Dell Latitude 5400, Windows 11 Pro 26200 |
| Module | Dell **DW5820e** = **Fibocom L850-GL**, Intel **XMM7360** LTE-A Cat 9 |
| PCI ID | `PCI\VEN_8086&DEV_7360&SUBSYS_58201028` ("Intel XMM7360-P WWAN") |
| Firmware | `18500.5001.00.05.27.12`, config element ver `5006.18` |
| IMEI | *(redacted — see `CLAUDE.local.md`, not in git)* |
| SIM | Hutch Sri Lanka (MCC/MNC 413-08), prepaid, SMSC `+94785000005`, MSISDN not stored on SIM |

## Windows driver architecture (important)

- Intel/Fibocom do **not** expose a real USB MBIM modem. `Ude.sys` (UDE 0.9.4.586) emulates
  USB devices on top of PCIe, and **`WinIhvRil.dll`** (runs inside WUDFHost, "ModemControl Device")
  translates Windows MBIM requests into AT commands.
- Emulated USB functions: `VID_8087&PID_0AC9` (MBIM net "DW5820e Intel 7360 LTE-A"),
  `VID_8087&PID_0ACA` MI_00 = **COM6 "MBIM-AT"**, MI_02 = **COM5 "CDC ACM Modem Control1"**,
  MI_04 = ModemControl (WUDF), `VID_8087&PID_0AF1` = GNSS location sensor.
- **COM5/COM6 are held exclusively by the RIL** → "Access denied" even as Administrator.
  Do not try to use them.
- **AT commands are available through an MBIM device service** instead:
  service `{8bae8180-b34b-4af8-b275-0178b52bcc19}`, SET command id **1**, payload = AT text,
  response = AT text. Fibocom's own `FwSwitchService.exe` uses this ("RPC AT channel");
  see `C:\ProgramData\Fibocom L850 Driver Package\logs\FwSwitchService.exe.log`.
  PowerShell 5.1 can't marshal the IBuffer for `SendSetCommandAsync` → use C#.
- Vendor files: `C:\Program Files (x86)\Fibocom L850\` (FiboConfigTool, FwSwitchService, RIL config XML).
- **The MBIM AT tunnel is whitelisted** (tested 2026-11-20 with `app` CLI `cellular at ...`):
  works: `ATI`, `AT+GTLOCKCAR?` (`0,65535`), `AT+GTASELCAR?` (`1`), `AT+GTMSELCAR?` (`65535`);
  `AT+GTMCFWVER?` timed out; everything standard (`AT`, `AT+COPS?`, `AT+CREG?`, `AT+CSQ`,
  `AT+CPIN?`, `AT+CIMI`, `AT+XACT?`, `AT+CMEE=2`, `AT+CGSN`…) returns bare `Error`.
  → No general AT access on this Windows setup: can't query/set RAT, CS/PS mode, voice, or do AT USSD.
  Real AT access would need Linux (`xmm7360-pci` driver / ModemManager) or the module in a USB adapter.
- `netsh mbn` cannot force network registration, **but the Mobile Broadband COM API can**:
  `IMbnRegistration::SetRegisterMode(MBN_REGISTER_MODE_AUTOMATIC, NULL, dataClass)` (no admin needed).
  Implemented in `app/src/Cellular.Core/MbnRegistration.cs`; CLI `cellular register [auto|lte|3g]`, `cellular regmode`.
  Found via the third-party "Mobile Broadband SMS Toolkit" (black-it.eu), which the user ran to recover the modem.

## Capabilities (as reported by Windows / MBIM)

- Voice class: **No voice** (data-only firmware). No audio path to the laptop codec on M.2 → no calling possible.
- SMS: PDU send + receive. `SmsDevice2` reachable from an ordinary desktop process.
- USSD: advertised (`WWAN_CTRL_CAPS_USSD`) and `UssdSession` can be created.
- Data classes: UMTS, HSDPA, HSUPA, HSPA+, LTE(-Advanced). 8 max contexts.
- Inbox "Messaging" app = operator-message viewer only (receive-only by design); that is why it can't send.

### Voice / "3G voice" check (2026-11-20)
- Windows' voice class is a **device** property, not per-RAT: `WwanVoiceClassNoVoice` regardless of LTE/3G.
- Could not query 3G CS capability (`AT+CEMODE?`, `AT+XACT?`, `AT+CLCC`…) because the AT tunnel is whitelisted.
- Even if the baseband could place a CS call on 3G, the M.2 card has no audio path to the laptop codec
  and Windows has no voice API for MBIM modems → **calling is not achievable on this hardware/OS combo.**
  Alternatives: phone + Phone Link, VoIP over LTE data, or a USB voice-capable dongle.

## Incidents

### 2026-11-20 — USSD knocked modem off the network
- Sent USSD `*344#` via `UssdSession` while on LTE → `0x800710DF` (device not ready).
  Immediately afterwards the modem **deregistered** and kept cycling Searching → Deregistered,
  signal 0 / −113 dBm. A second attempt (`*123#`) failed with "device busy or unavailable".
- `MobileBroadbandNetwork.RegistrationNetworkError = 4` (3GPP cause #4 "IMSI unknown in VLR" —
  a CS-domain reject), consistent with the firmware trying CS fallback for USSD on a data-only SIM/module.
- Did NOT recover: radio off/on (`netsh mbn set powerstate`), `MobileBroadbandModem.ResetAsync`
  (0x80070032 not supported), disable/enable PCI device as admin.
- Also note `netsh mbn show visibleproviders` scan failed with 0x40040009 just before.
- **Lesson: don't fire USSD blindly on this module; check registration first, and expect USSD may
  need a 3G/CS-capable registration.**
- SMS send attempted while deregistered → failed (`ModemErrorCode=Other`) — expected, no network.
- **2026-11-21 00:22 Windows restart did NOT fix it.** After boot: SIM initialized, radio on, HomeProvider=Hutch 41308,
  then a ~90 s loop Searching → Deregistered, still `RegistrationNetworkError=4`, −113 dBm.
  `netsh mbn show visibleproviders` → 0x40040009; `MobileBroadbandNetwork.GetCellsInfoAsync` → 0x80070102 (timeout).
  A warm restart keeps the M.2 slot powered, so the baseband never truly reset.
- Theory: after a reject (3GPP 24.008/24.301) the UE can mark the USIM/PLMN as invalid **until power-off or
  UICC removal**. Next steps: full shutdown (`shutdown /s /t 0`, ≥30 s off) and/or re-seat the SIM; test the SIM in a phone
  to rule out the network/account side.
- Windows Settings "Network type: 3GPP" = cellular *family* (GSM/UMTS/LTE vs 3GPP2/CDMA), not the RAT — always 3GPP; not a symptom.
- **RESOLVED 2026-11-21 00:37** without reboot: a single `SET RegisterState Action=Automatic` (sent by the toolkit) →
  Home/HUTCH/LTE within 2 s. The modem had simply stopped re-registering on its own.

### 2026-11-21 — follow-up tests (modem back on Hutch LTE, −83 dBm)
- **USSD on LTE reproducibly drops registration**: `*344#` → "not ready" (0x800710DF) → Deregistered within seconds.
  A register request sent while "Deregistered" was ignored; the next one, once it was "Searching", registered in ~2 s
  → `MbnRegistration.RecoverAsync` repeats the request every 15 s.
- **Preferring 3G** (`SetRegisterMode` with dataClass UMTS|HSDPA|HSUPA = 0x1C) → modem deregistered and stayed off
  (no usable Hutch 3G here / module won't). Restored with dataClass 0. So "switch to 3G for USSD" is not viable.
- **SMS send works at the protocol level**: fails instantly (~350 ms) with `SmsModemErrorCode.Other`, network cause 0,
  and Hutch replies with a class-0 (flash) SMS — the user confirmed it said **insufficient balance**. Need credit to test MO SMS.
  International (`+94…`) and local (`07…`) formats behave the same.
- **SMS receive works** (legacy `SmsDevice` store read succeeds once registered; got a Hutch Self-care OTP).
- `SmsRouter` event 102 "GetWwanInterfaceObject … parameter is incorrect" (1600+ times since Aug) fires at boot only — noise.
- WwanSvc never logs SMS operations; use timing / SmsRouter log (301 text, 304 class-0 received) instead.
- **Cell tower / neighbor info is NOT available** (checked while registered on LTE):
  `MobileBroadbandNetwork.GetCellsInfoAsync` → 0x80548212 E_MBN_FAILURE after 6–8 s (3/3 tries);
  `netsh mbn show basestationinfo` → "The request is not supported" (despite "Base-stations Info" in optional caps);
  AT `+GTCCINFO?`, `+XCELLINFO?`, `+CSQ`, `+CESQ`, `+XCESQ?`, `+COPS?`, `+GTSINR?` → Error; `+XMCI=1` → timeout.
  Only serving-network data exists: `netsh mbn show signal` (RSSI dBm, error rate) and `show netlteattachinfo` (APN `internet`).
- **RSRP / RSRQ / SINR are NOT available**: no WinRT/mbnapi API exposes them, `GetCellsInfoAsync` fails, AT is blocked,
  and raw MBIM on Windows' native services (Basic Connect Ext `3d01dcc5…`, CIDs 3,4,6,9,11,12 listed) is refused —
  `OpenCommandSession` → 0x80070015 ERROR_NOT_READY (reserved for WwanSvc). Only RSSI dBm, signal % and the
  MBIM error-rate class (`netsh mbn show signal`) exist; the app shows those plus "not reported" for RSRP/RSRQ/SINR.
- **Cell/tower name ("cell info display")**: feature phones get it from Cell Broadcast channel 50, which the modem must
  be told to listen to (`AT+CSCB`) — blocked by the AT whitelist, and Windows only routes emergency broadcasts. Not possible here.

### 2026-11-21 — balance via SMS (USSD replacement)
- Hutch prepaid: **SMS `BAL` to 344** (hutch.lk/account-balance), free; reply arrives in ~1 s as a 2-part SMS:
  `Bal 1 Rs 50.08 Ex 20/05/2027, Cheer Points  1031.90 Ex 31/01/2027, Hotspot Anytime Data 50.00 GB Ex 21/12/2026,
  Bonus Anytime Data 12582912.00 Byte Ex 21/11/2026, …, Status : Active` (data in GB **or Byte**; items separated by ", ").
  Reload notices: `Reloaded Rs.50.00. AC Bal 50.08 Dial *344# for all bal`.
- **Hutch SMSC timestamps run several minutes behind** local time → use local receive time for "updated" logic.
- **Windows' SMS decoding is broken for these** (`SmsTextMessage.FromBinaryMessage`): ignores UDL → RIL zero padding
  shows as "@@@@", and doesn't join concatenated parts. App decodes PDUs itself: `Cellular.Core/SmsPdu.cs`
  (SMS-DELIVER, GSM-7/UCS-2, alphanumeric OA, UDH concat 8/16-bit, SmsAssembler for multipart).
- Store PDUs were sometimes truncated (~50 bytes of text, zero-filled) in early bulk listings; later listings and
  `GetMessageAsync(id)` returned full PDUs. Decoder detects zero-fill and marks text with "…".
- Modem store **slot ids are reused** (1..N) → dedupe by slot + network timestamp (±1 min), never by text alone
  (operators send identical texts).
- Legacy `SmsMessageReceivedEventArgs.BinaryMessage` **crashes the process (AV)** — only use `TextMessage`.
- The legacy `SmsMessageReceived` event **fires in a console app but not inside the WPF app** (tried creating the
  device on an MTA thread too). App therefore polls the store: every 5 s while a balance request is pending, and every
  2 min for the inbox. Bulk store listing occasionally hangs → always wrap in a timeout.
- CLI helpers: `cellular sms-raw [--byid]`, `sms-listen <secs> [to text]`, `parse "<text>"`, `services`, `mbim-query`.
- App diagnostics: `%LOCALAPPDATA%\CellularStudio\activity.log` (activity log mirror) and `trace.log` (window
  close/visibility stack traces — added after the main window was seen hidden twice without a close action; cause unknown).
- **Dev gotcha**: PowerShell 5.1 `Get-Content -Raw` reads BOM-less UTF-8 as ANSI → rewriting with Set-Content garbles
  `·—…`. Use `[IO.File]::ReadAllText(p, UTF8)` / the Edit tool for source edits.

## App (`app/`) — Cellular Studio

- C# / .NET 10, WPF + MVVM (CommunityToolkit.Mvvm). Solution `app/CellularStudio.slnx`; details in `app/README.md`.
- Build/publish with the per-user SDK: `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` (installed 2026-11-20 via dotnet-install.ps1;
  `C:\Program Files\dotnet` has no SDK). Output: `app/publish/CellularStudio.exe` (self-contained, ~78 MB) and `app/publish/cli/cellular.exe`.
- APIs used: `SmsDevice2` (send), legacy `SmsDevice` (message store + `SmsMessageReceived`), `UssdSession`,
  `MobileBroadbandAccount/Modem` (status), `netsh mbn show interfaces|capability` (signal dBm, IMEI, voice class).
  `MobileBroadbandDeviceInformation.DeviceId` is a device path, NOT the IMEI.
- WinRT `DataClasses` enum has no plain `Lte`; MBIM LTE bit (0x20) is checked manually.
- Tray: closing hides to tray; Exit only from tray menu. `--tray` arg = start hidden. Startup via HKCU Run value `CellularStudio`.
  Single instance via named mutex + `…Activate` event.
- Safety: app refuses SMS/USSD while not registered (avoids repeating the 2026-11-20 incident).
- Recovery: offline banner has **Reconnect** (mbnapi automatic registration, retried every 15 s up to 2 min); the app also
  auto-recovers after 2 min stuck Searching/Deregistered (10 min cooldown), and right after any failed USSD.
- USSD is **blocked on LTE by default** (explanatory card + "Try anyway" switch, persisted as `AllowUssdOnLte` in data.json).
- "Other / cause 0" SMS failure is shown as "network refused — usually not enough credit".
- Carrier badge: monogram in brand color; real logo if `%LOCALAPPDATA%\CellularStudio\logos\<mccmnc|name>.png` exists.
- Dev helpers: `app/tools/screenshot.ps1` (PrintWindow capture), `app/tools/nav.ps1` (UIA page switch), `app/tools/make-icon.ps1`.
- Windows DPI: screen work area is ~690 DIP tall at 150% → window size is clamped to the work area at startup.

### Multi-carrier support (2026-11-21, v1.1.0)
- Profiles in `Cellular.Core/Carriers.cs`, keyed `hutch` / `dialog` / `mobitel`. MNC map (Wikipedia ITU 4xx list):
  01,12 SLTMobitel · 02,05(ex-Airtel),11 Dialog · 08,03(ex-Etisalat),09 Hutch. Name-based fallback only for MCC 413/unknown
  (Indian "Airtel" must not become Dialog).
- **Only Hutch's SMS balance method is verified.** Researched dialog.lk (self-help, dial-codes, IVR, help-guide, faq) and
  sltmobitel.lk (JS-only pages): no SMS balance keyword/number published. Dialog documents `#678#` menu and IVR 1777 /
  077 7123 456. Do NOT hard-code Dialog/Mobitel keywords from memory — users enter them (Device → Carrier & balance),
  stored as `CarrierOverrides` in data.json. Brand colours for Dialog/SLTMobitel are approximations (not found officially).
- Logos: user picks an image → `%LOCALAPPDATA%\CellularStudio\logos\<key>.png`; no operator logos are shipped (trademarks).
- BalanceParser is carrier-agnostic (credit/expiry/data phrasings, dd/mm/yyyy, yyyy-mm-dd, 21-Dec-2026); test with
  `cellular parse "<text>"`; resolution with `cellular carrier <mccmnc> [name]`. Only Hutch tested with real replies.
- Saved balance is tied to the SIM's IMSI (`LastBalanceImsi`, local only); a different SIM clears it.

### Installer (2026-11-21)
- WiX Toolset **v5.0.2** as a global dotnet tool (`%USERPROFILE%\.dotnet\tools\wix.exe`) + `WixToolset.UI.wixext`,
  `WixToolset.Util.wixext` (global extension cache). Needs `DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet` and
  `DOTNET_ROLL_FORWARD=Major` (only .NET 10 runtime present). `app/installer/build-installer.ps1` sets both.
- `app/installer/Package.wxs`: per-user MSI (`Scope="perUser"`) → `%LOCALAPPDATA%\Programs\Cellular Studio`
  (ProgramFilesFolder would need admin under a pure per-user scope). UpgradeCode `CA4701A0-48DE-4B7E-A780-16590AD65C67`
  — never change it. WixUI_FeatureTree with custom banner/dialog BMPs (`make-artwork.ps1`) and License.rtf.
- `util:CloseApplication` with `TerminateProcess` is required because WM_CLOSE only hides the app to the tray.
- Uninstall runs `reg delete …\Run /v CellularStudio` (WixQuietExec), skipped during upgrades (`NOT UPGRADINGPRODUCTCODE`).
- Verified 2026-11-21: silent install, upgrade over a running app (closed, Run value kept), uninstall with app running
  (all removed, user data kept), wizard pages visually checked. Per-user MSIs register via Windows Installer
  (`ProductsEx`), not the HKCU Uninstall key.
- Output: `app/dist/CellularStudio-<ver>-x64.msi` (~73 MB, self-contained runtime inside).

### GitHub (2026-11-21)
- Public repo **https://github.com/surin721/CellularStudio** (branch `main`), release **v1.0.0** with
  `CellularStudio-1.0.0-x64.msi` (SHA-256 `3B65242274E8331A26F4443781F2CA7F121B1F9A2F2A79AB4CE92BA89F684B6B`).
- gh CLI (per-user winget install): `%LOCALAPPDATA%\Microsoft\WinGet\Packages\GitHub.cli_Microsoft.Winget.Source_8wekyb3d8bbwe\bin\gh.exe`;
  `gh auth setup-git` done. Repo-local commit email = GitHub noreply (global git email is a work address — keep it out).
- **Public repo rule:** device/SIM identifiers and the user's phone number live only in `CLAUDE.local.md` (git-ignored).
  Before every commit: `git grep --cached` for them. No screenshots of the Device/Messages pages.
- New release: bump `<Version>` in `app/Directory.Build.props` → `app/installer/build-installer.ps1` →
  `gh release create vX.Y.Z app/dist/CellularStudio-X.Y.Z-x64.msi app/installer/CellularStudio-codesign.cer --notes-file …`.
- License: **MIT** (`LICENSE`, © 2026 Surin Athukorala); also in installer License.rtf and assembly Copyright.

### Code signing (2026-11-21)
- **Self-signed** cert `CN=Surin Athukorala (Cellular Studio code signing)`, RSA-3072/SHA-256, thumbprint
  `0FA0531163DABC4B48E009C2F266C9004864C526`, valid to 2031-11-21. Private key in `Cert:\CurrentUser\My`
  (exportable). **Backup** (2026-11-21): password-protected PFX (AES256-SHA256) at
  `%USERPROFILE%\OneDrive\Documents\CellularStudio-signing-backup\CellularStudio-codesign-0FA05311.pfx`
  — Documents is OneDrive-redirected, so it syncs to the cloud. Password chosen by the user, stored only by them.
  Never copy the PFX into the repo. Restore: `Import-PfxCertificate -FilePath <pfx> -CertStoreLocation Cert:\CurrentUser\My -Password (Read-Host -AsSecureString)`. Public part:
  `app/installer/CellularStudio-codesign.cer` (committed, also attached to releases).
- Trusted on this PC via `certutil -user -addstore Root|TrustedPublisher` (Root needs a user click; the tool's
  non-interactive PowerShell can't show that dialog → run certutil as a separate process).
- Signing = `Set-AuthenticodeSignature` (Windows SDK/signtool not installed), timestamp `http://timestamp.digicert.com`.
  Signing the .NET single-file exe after publish works (verified it still runs). Exe is signed **before** WiX packs it.
- Switching to a real CA / Azure Trusted Signing cert later: import it and pass `-CertThumbprint`.

## Open items
- [x] Recover modem registration (done via SetRegisterMode, no reboot).
- [x] SMS receive verified. USSD on LTE verified NOT working (drops registration) → blocked by default in app.
- [ ] Top up credit, then verify MO SMS to the test number (see `CLAUDE.local.md`).
- [x] Balance via SMS `BAL`→344 implemented and verified end-to-end (tiles + data-package breakdown).
- [x] MO SMS verified working after top-up (BAL to 344 sends fine).
- [ ] Find out why the legacy receive event doesn't fire in the WPF process (polling works around it).
- [ ] Watch `trace.log` for the unexplained main-window hide.

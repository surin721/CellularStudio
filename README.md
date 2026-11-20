# Cellular Studio

A Windows app for laptops with a built-in LTE modem. It was built for the **Dell DW5820e / Fibocom L850-GL
(Intel XMM7360)** on a Sri Lankan Hutch SIM. Windows' own Messaging app can only *show* operator messages; this app can:

- **send and receive SMS**, with conversations, a GSM-7/Unicode segment counter, and correct decoding of
  multi-part operator messages (Windows' decoder shows "@@@@" for them);
- **show your balance** (credit, data packages with expiry dates, validity) by sending the operator's free SMS
  balance query and reading the reply (Hutch: `BAL` to 344);
- **keep the modem online**: it detects when the modem is stuck "Searching" and re-registers it automatically;
- show modem, SIM and network details (RSSI, signal level, error rate) and the modem's real capabilities;
- run from the **system tray**, optionally starting with Windows.

## Download

Get the MSI from [Releases](../../releases). It installs per-user and needs no admin rights.

### Code signing
The MSI and the app are Authenticode-signed and timestamped with a **self-signed** certificate,
*Surin Athukorala (Cellular Studio code signing)*, SHA-1 thumbprint `0FA0531163DABC4B48E009C2F266C9004864C526`.
A self-signed certificate proves the files haven't been modified since signing, but Windows doesn't trust it by default.
On a new PC you'll see "Unknown publisher" and a SmartScreen prompt (*More info → Run anyway*) unless you trust the certificate first:

1. Download [`CellularStudio-codesign.cer`](app/installer/CellularStudio-codesign.cer) (also attached to each release).
2. Check that its thumbprint matches the one above (*double-click → Details → Thumbprint*).
3. Trust it for your user account (PowerShell):
   ```powershell
   certutil -user -addstore Root .\CellularStudio-codesign.cer
   certutil -user -addstore TrustedPublisher .\CellularStudio-codesign.cer
   ```
   To undo this, delete the certificate from *Trusted Root Certification Authorities* and *Trusted Publishers* in `certmgr.msc`.

Only trust a certificate whose thumbprint you've verified.

## License

[MIT](LICENSE) © 2026 Surin Athukorala

## Repository layout

| Path | What |
|---|---|
| [`app/`](app/) | The app: C# / .NET 10, WPF. Build, install and usage notes are in [`app/README.md`](app/README.md). |
| [`app/installer/`](app/installer/) | WiX v5 per-user MSI (`build-installer.ps1`). |
| [`CLAUDE.md`](CLAUDE.md) | Engineering log of what was learned about this modem on Windows: driver architecture, the whitelisted AT tunnel, USSD dropping LTE registration, SMS PDU quirks, and what is *not* possible (voice calls, RSRP/RSRQ/SINR, cell IDs). |

## Known limitations of this modem on Windows

- **No voice calls.** The firmware reports "no voice" and the M.2 card has no audio path to the laptop.
- **USSD on LTE fails** and knocks the modem off the network, so the app uses SMS for the balance instead.
- **Cell info and LTE signal metrics** (RSRP/RSRQ/SINR, cell IDs, neighbouring towers) aren't exposed by the driver.

Details and evidence are in [`CLAUDE.md`](CLAUDE.md).

First release of **Cellular Studio**: SMS, balance and modem tools for laptops with the Dell DW5820e / Fibocom L850-GL (Intel XMM7360) LTE modem.

### Highlights
- **SMS:** send and receive, with conversations and a segment counter. Long multi-part operator messages decode correctly (no more "@@@@").
- **Balance on the Overview page:** credit, total data with a per-package breakdown and expiry dates, and validity. It's fetched with the operator's free SMS query (Hutch: `BAL` to 344), refreshed automatically when older than 6 h, and also picked up from reload notices.
- **Connection recovery:** if the modem gets stuck "Searching", the app re-registers it (a **Reconnect** button, plus automatic recovery after 2 min).
- **Device page:** modem, SIM and network details, RSSI, signal level, error rate, capabilities, and an activity log.
- **System tray:** close hides to the tray, Exit is in the tray menu, and it can optionally start with Windows (hidden).

### Install
Download `CellularStudio-1.0.0-x64.msi` below and run it.
- Per-user install, **no admin rights needed**, into `%LOCALAPPDATA%\Programs\Cellular Studio`.
- Custom Setup page: *Start with Windows* (on by default) and *Desktop shortcut* (off by default).
- Self-contained: no .NET installation required.
- The MSI isn't code-signed, so SmartScreen may warn. Choose **More info → Run anyway**.

### Known limitations
- USSD doesn't work on LTE with this modem: it fails and drops the network connection, so it's blocked by default.
- No voice calls (data-only firmware, no audio path on M.2).
- RSRP/RSRQ/SINR, cell IDs and neighbouring towers aren't available from the Windows driver.

Requires Windows 10 2004 or later (tested on Windows 11), x64, and an MBIM mobile broadband modem.

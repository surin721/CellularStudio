### What's new in 1.0.1
- **Signed:** the installer and the app are now Authenticode-signed (SHA-256, timestamped).
- **MIT License:** the project is now [MIT-licensed](https://github.com/surin721/CellularStudio/blob/main/LICENSE). The installer's licence page and the app's file properties show it.
- The installer's publisher is now *Surin Athukorala*.

No functional changes since 1.0.0. Installing 1.0.1 upgrades 1.0.0 in place and keeps your settings and messages.

### About the signature
The certificate is **self-signed**: *Surin Athukorala (Cellular Studio code signing)*, SHA-1 thumbprint
`0FA0531163DABC4B48E009C2F266C9004864C526`, valid until 2031.

It proves the files are unmodified, but Windows doesn't trust a self-signed certificate by default. On a new PC you'll still see
"Unknown publisher" and a SmartScreen prompt (*More info → Run anyway*). To trust it, download `CellularStudio-codesign.cer` below,
**check that the thumbprint matches**, then run (PowerShell):

```powershell
certutil -user -addstore Root .\CellularStudio-codesign.cer
certutil -user -addstore TrustedPublisher .\CellularStudio-codesign.cer
```

### Install
Run `CellularStudio-1.0.1-x64.msi`. It's a per-user install that needs no admin rights and no separate .NET install.
See the [1.0.0 release](https://github.com/surin721/CellularStudio/releases/tag/v1.0.0) for the full feature list and known limitations.

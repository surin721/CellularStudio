using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace Cellular.Core;

public enum RegistrationState { Unknown, NoModem, Off, Deregistered, Searching, Home, Roaming, Denied }

public sealed record ModemStatus
{
    public bool ModemPresent { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string Firmware { get; init; } = "";
    public string Imei { get; init; } = "";
    public string Imsi { get; init; } = "";
    public string Iccid { get; init; } = "";
    public string SimSpn { get; init; } = "";
    public string PhoneNumber { get; init; } = "";
    public bool RadioOn { get; init; }
    public RegistrationState Registration { get; init; }
    public string ProviderName { get; init; } = "";
    public string ProviderId { get; init; } = "";
    public string NetworkType { get; init; } = "";
    public int SignalPercent { get; init; }
    public int? RssiDbm { get; init; }
    public int SignalBars { get; init; }
    /// <summary>MBIM error-rate class 0–7 (99 = unknown); from `netsh mbn show signal`.</summary>
    public int? ErrorRate { get; init; }
    public bool DataConnected { get; init; }
    public uint RegistrationError { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public bool IsRegistered => Registration is RegistrationState.Home or RegistrationState.Roaming;

    /// <summary>Best display name for the carrier: network name, else SIM SPN.</summary>
    public string CarrierDisplayName => !string.IsNullOrWhiteSpace(ProviderName) ? ProviderName
        : !string.IsNullOrWhiteSpace(SimSpn) ? SimSpn : "No carrier";

    /// <summary>MCC+MNC used for branding: registered network, else derived from IMSI.</summary>
    public string BrandingId => !string.IsNullOrEmpty(ProviderId) ? ProviderId
        : Imsi.Length >= 5 ? Imsi[..5] : "";
}

/// <summary>Reads the modem/SIM/network state from WinRT, with netsh for signal data.</summary>
public static class ModemStatusReader
{
    public static async Task<ModemStatus> ReadAsync(CancellationToken ct = default)
    {
        var accountId = MobileBroadbandAccount.AvailableNetworkAccountIds.FirstOrDefault();
        if (accountId is null) return new ModemStatus { ModemPresent = false, Registration = RegistrationState.NoModem };

        var account = MobileBroadbandAccount.CreateFromNetworkAccountId(accountId);
        var dev = account.CurrentDeviceInformation;
        var net = account.CurrentNetwork;
        var netsh = await ReadNetshAsync(ct);

        var reg = net.NetworkRegistrationState switch
        {
            NetworkRegistrationState.Home => RegistrationState.Home,
            NetworkRegistrationState.Roaming or NetworkRegistrationState.Partner => RegistrationState.Roaming,
            NetworkRegistrationState.Searching => RegistrationState.Searching,
            NetworkRegistrationState.Denied => RegistrationState.Denied,
            NetworkRegistrationState.Deregistered => RegistrationState.Deregistered,
            NetworkRegistrationState.None => RegistrationState.Deregistered,
            _ => RegistrationState.Unknown,
        };
        var radioOn = dev.CurrentRadioState == MobileBroadbandRadioState.On;
        if (!radioOn) reg = RegistrationState.Off;

        int bars = 0;
        bool connected = false;
        try
        {
            var profile = NetworkInformation.GetConnectionProfiles().FirstOrDefault(p => p.IsWwanConnectionProfile);
            if (profile is not null)
            {
                bars = profile.GetSignalBars() ?? 0;
                connected = profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None;
            }
        }
        catch { /* ignore */ }
        if (bars == 0 && netsh.SignalPercent > 0) bars = (int)Math.Ceiling(netsh.SignalPercent / 20.0);

        string phone = "";
        try { phone = dev.TelephoneNumbers.FirstOrDefault() ?? ""; } catch { }

        return new ModemStatus
        {
            ModemPresent = true,
            Manufacturer = dev.Manufacturer,
            Model = dev.Model,
            Firmware = dev.FirmwareInformation,
            Imei = netsh.Imei,
            Imsi = dev.SubscriberId,
            Iccid = dev.SimIccId,
            SimSpn = Safe(() => dev.SimSpn),
            PhoneNumber = phone,
            RadioOn = radioOn,
            Registration = reg,
            ProviderName = net.RegisteredProviderName ?? "",
            ProviderId = net.RegisteredProviderId ?? "",
            NetworkType = DescribeDataClass(net.RegisteredDataClass),
            SignalPercent = netsh.SignalPercent,
            RssiDbm = netsh.RssiDbm,
            SignalBars = Math.Clamp(bars, 0, 5),
            ErrorRate = reg is RegistrationState.Home or RegistrationState.Roaming ? await ReadErrorRateAsync(ct) : null,
            DataConnected = connected && netsh.Connected,
            RegistrationError = net.RegistrationNetworkError,
        };
    }

    private static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

    public static string DescribeDataClass(DataClasses dc)
    {
        if (dc.HasFlag(DataClasses.NewRadioStandalone) || dc.HasFlag(DataClasses.NewRadioNonStandalone)) return "5G";
        // WinRT has no plain "LTE" flag; MBIM LTE (0x20) arrives as LteAdvanced or Custom on this driver.
        if (dc.HasFlag(DataClasses.LteAdvanced) || ((uint)dc & 0x20) != 0) return "LTE";
        if (dc.HasFlag(DataClasses.Hsdpa) || dc.HasFlag(DataClasses.Hsupa)) return "3G+";
        if (dc.HasFlag(DataClasses.Umts)) return "3G";
        if (dc.HasFlag(DataClasses.Edge)) return "EDGE";
        if (dc.HasFlag(DataClasses.Gprs)) return "GPRS";
        return dc == DataClasses.None ? "" : dc.ToString();
    }

    private sealed record NetshInfo(int SignalPercent, int? RssiDbm, bool Connected, string Imei = "");

    private static async Task<NetshInfo> ReadNetshAsync(CancellationToken ct)
    {
        try
        {
            var text = await RunAsync("netsh", "mbn show interfaces", ct);
            int pct = 0; int? dbm = null;
            var m = Regex.Match(text, @"Signal\s*:\s*(\d+)%");
            if (m.Success) pct = int.Parse(m.Groups[1].Value);
            m = Regex.Match(text, @"RSSI / RSCP\s*:\s*\d+\s*\((-?\d+)\s*dBm\)");
            if (m.Success) dbm = int.Parse(m.Groups[1].Value);
            if (dbm <= -113 && pct == 0) dbm = null;
            var connected = Regex.IsMatch(text, @"^\s*State\s*:\s*Connected", RegexOptions.Multiline);
            var imei = Regex.Match(text, @"Device Id\s*:\s*(\d{14,16})").Groups[1].Value;
            return new NetshInfo(pct, dbm, connected, imei);
        }
        catch { return new NetshInfo(0, null, false); }
    }

    private static async Task<int?> ReadErrorRateAsync(CancellationToken ct)
    {
        try
        {
            var text = await RunAsync("netsh", "mbn show signal interface=*", ct);
            var m = Regex.Match(text, @"Error Rate\s*:\s*(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
        catch { return null; }
    }

    internal static async Task<string> RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return output;
    }
}

/// <summary>Static capability info (voice/SMS/USSD/data classes) from `netsh mbn show capability`.</summary>
public sealed record ModemCapabilities(string VoiceClass, string SmsCaps, string ControlCaps, string DataClasses, string OptionalCaps)
{
    public bool HasVoice => !VoiceClass.Contains("No voice", StringComparison.OrdinalIgnoreCase) && VoiceClass.Length > 0;
    public bool CanSendSms => SmsCaps.Contains("send", StringComparison.OrdinalIgnoreCase);
    public bool CanReceiveSms => SmsCaps.Contains("receive", StringComparison.OrdinalIgnoreCase);
    public bool HasUssd => ControlCaps.Contains("USSD", StringComparison.OrdinalIgnoreCase);

    public static async Task<ModemCapabilities?> ReadAsync(CancellationToken ct = default)
    {
        try
        {
            var text = await ModemStatusReader.RunAsync("netsh", "mbn show capability interface=*", ct);
            string Field(string name)
            {
                // value may continue on following indented lines that contain no colon
                var m = Regex.Match(text, $@"^\s*{Regex.Escape(name)}\s*:\s*(.*(?:\r?\n\s{{20,}}[^:\r\n]+)*)", RegexOptions.Multiline);
                return m.Success ? Regex.Replace(m.Groups[1].Value, @"\s*\r?\n\s+", ", ").Trim() : "";
            }
            return new ModemCapabilities(Field("Voice class"), Field("SMS capability"), Field("Control capability"),
                Field("Data class"), Field("Optional capabilities"));
        }
        catch { return null; }
    }
}

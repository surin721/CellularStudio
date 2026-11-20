using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cellular.Core;

public enum MessageDirection { Incoming, Outgoing }
public enum MessageStatus { Received, Sending, Sent, Failed }

public sealed class StoredMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Address { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public MessageDirection Direction { get; set; }
    public MessageStatus Status { get; set; }
    public string? Error { get; set; }
    public uint? StoreId { get; set; }
    public bool Read { get; set; }
}

public sealed class UssdLogEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Request { get; set; } = "";
    public string Response { get; set; } = "";
    public bool Failed { get; set; }
}

public sealed class AppData
{
    public List<StoredMessage> Messages { get; set; } = [];
    public List<UssdLogEntry> UssdHistory { get; set; } = [];
    public BalanceInfo? LastBalance { get; set; }
    public string? LastBalanceRaw { get; set; }
    public DateTimeOffset? LastBalanceAt { get; set; }
    /// <summary>IMSI of the SIM the saved balance belongs to (local only), so a different SIM doesn't show it.</summary>
    public string? LastBalanceImsi { get; set; }
    public string? DataBalanceCode { get; set; }
    public string? MyNumber { get; set; }
    /// <summary>USSD on LTE fails and drops registration on the XMM7360/Hutch; off unless the user opts in.</summary>
    public bool AllowUssdOnLte { get; set; }
    /// <summary>Per-carrier user overrides (balance SMS method, reply senders), keyed by CarrierBrand.Key.</summary>
    public Dictionary<string, CarrierOverride> CarrierOverrides { get; set; } = [];
}

/// <summary>Local JSON persistence under %LOCALAPPDATA%\CellularStudio.</summary>
public static class AppStorage
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CellularStudio");

    private static string DataFile => Path.Combine(Root, "data.json");
    private static readonly object Lock = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppData Load()
    {
        try
        {
            if (File.Exists(DataFile))
                return JsonSerializer.Deserialize<AppData>(File.ReadAllText(DataFile), Options) ?? new AppData();
        }
        catch { /* corrupt file: start fresh but keep a copy */ TryBackup(); }
        return new AppData();
    }

    public static void Save(AppData data)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Root);
            var tmp = DataFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options));
            File.Move(tmp, DataFile, overwrite: true);
        }
    }

    private static void TryBackup()
    {
        try { File.Copy(DataFile, DataFile + $".bad-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
    }
}

using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Networking.NetworkOperators;

namespace Cellular.Core;

/// <summary>
/// AT command channel for the Intel XMM7360 (Fibocom L850-GL / Dell DW5820e).
/// The physical AT ports (COM5/COM6) are held exclusively by the vendor RIL, so commands are
/// tunnelled through the vendor MBIM device service that Fibocom's own tools use ("RPC AT channel").
/// </summary>
public sealed class AtChannel
{
    public static readonly Guid ServiceId = new("8bae8180-b34b-4af8-b275-0178b52bcc19");
    private const uint SetCommandId = 1;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsAvailable()
    {
        try
        {
            var modem = MobileBroadbandModem.GetDefault();
            return modem?.DeviceServices.Any(s => s.DeviceServiceId == ServiceId) == true;
        }
        catch { return false; }
    }

    /// <summary>Sends one AT command and returns the raw response text (without echo).</summary>
    public async Task<AtResponse> SendAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var modem = MobileBroadbandModem.GetDefault() ?? throw new ModemException("No mobile broadband modem found.");
        var svc = modem.GetDeviceService(ServiceId) ?? throw new ModemException("Vendor AT service is not exposed by this modem.");

        await Gate.WaitAsync(ct);
        try
        {
            var session = svc.OpenCommandSession();
            try
            {
                var payload = Encoding.ASCII.GetBytes(command.TrimEnd('\r', '\n') + "\r").AsBuffer();
                var op = session.SendSetCommandAsync(SetCommandId, payload).AsTask(ct);
                var done = await Task.WhenAny(op, Task.Delay(timeout ?? TimeSpan.FromSeconds(20), ct));
                if (done != op) throw new ModemException($"Timed out waiting for response to {command}");
                var result = await op;
                var text = result.ResponseData is { Length: > 0 } buf
                    ? Encoding.ASCII.GetString(buf.ToArray()).Replace("\0", "")
                    : "";
                return AtResponse.Parse(command, text, result.StatusCode);
            }
            finally
            {
                try { session.CloseSession(); } catch { /* ignore */ }
            }
        }
        catch (Exception ex) when (ex is not ModemException and not OperationCanceledException)
        {
            throw new ModemException($"AT channel error for {command}: 0x{ex.HResult:X8} {ex.Message}".Trim(), ex);
        }
        finally
        {
            Gate.Release();
        }
    }
}

public sealed record AtResponse(string Command, string Raw, uint StatusCode)
{
    public IReadOnlyList<string> Lines { get; init; } = [];
    public bool Ok => Lines.Count > 0 && Lines[^1] == "OK";
    public string? Error => Lines.LastOrDefault(l => l.StartsWith("ERROR") || l.StartsWith("+CME ERROR") || l.StartsWith("+CMS ERROR"));

    /// <summary>Lines that carry a given prefix, with the prefix stripped (e.g. "+CSQ:").</summary>
    public IEnumerable<string> Values(string prefix) =>
        Lines.Where(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(l => l[prefix.Length..].Trim());

    public static AtResponse Parse(string command, string raw, uint status)
    {
        var lines = raw.Replace("\r", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.Equals(command.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new AtResponse(command, raw, status) { Lines = lines };
    }

    public override string ToString() => string.Join(Environment.NewLine, Lines);
}

public sealed class ModemException(string message, Exception? inner = null) : Exception(message, inner);

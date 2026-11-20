using Windows.Devices.Sms;

namespace Cellular.Core;

public sealed record SmsSendOutcome(bool Success, string? Error, bool Transient);

public sealed record IncomingSms(string From, string Body, DateTimeOffset Timestamp, uint StoreId)
{
    /// <summary>Store ids of all parts when this is a reassembled multipart message.</summary>
    public IReadOnlyList<uint> PartIds { get; init; } = [];
}

/// <summary>SMS send/receive over the modem's MBIM SMS service (Windows.Devices.Sms).</summary>
public sealed class SmsService : IDisposable
{
#pragma warning disable CS0618 // legacy SmsDevice still the only API with a message store + receive event for desktop apps
    private SmsDevice? _legacy;

    public event EventHandler<IncomingSms>? MessageReceived;
    /// <summary>Diagnostic trace of the receive pipeline (for the activity log).</summary>
    public event Action<string>? Diagnostic;

    public static int MaxCharsPerSegment(string text) => IsGsm7(text) ? 160 : 70;

    /// <summary>Returns (segments, charsLeftInCurrentSegment, encodingName).</summary>
    public static (int Segments, int Remaining, string Encoding) Measure(string text)
    {
        var gsm = IsGsm7(text);
        int len = gsm ? Gsm7Length(text) : text.Length;
        int single = gsm ? 160 : 70, multi = gsm ? 153 : 67;
        if (len <= single) return (len == 0 ? 0 : 1, single - len, gsm ? "GSM-7" : "Unicode");
        int segs = (int)Math.Ceiling(len / (double)multi);
        return (segs, segs * multi - len, gsm ? "GSM-7" : "Unicode");
    }

    private const string Gsm7Basic = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string Gsm7Ext = "^{}\\[~]|€\f";
    private static bool IsGsm7(string s) => s.All(c => Gsm7Basic.Contains(c) || Gsm7Ext.Contains(c));
    private static int Gsm7Length(string s) => s.Sum(c => Gsm7Ext.Contains(c) ? 2 : 1);

    public async Task<SmsSendOutcome> SendAsync(string to, string body, CancellationToken ct = default)
    {
        var device = SmsDevice2.GetDefault() ?? throw new ModemException("No SMS-capable modem found.");
        if (device.DeviceStatus != SmsDeviceStatus.Ready)
            return new SmsSendOutcome(false, $"SMS service is not ready ({device.DeviceStatus}). The modem is probably not registered on the network.", true);

        var msg = new SmsTextMessage2 { To = to.Trim(), Body = body };
        var result = await device.SendMessageAndGetResultAsync(msg).AsTask(ct);
        if (result.IsSuccessful) return new SmsSendOutcome(true, null, false);

        var why = result.ModemErrorCode switch
        {
            SmsModemErrorCode.NetworkNotReady or SmsModemErrorCode.NetworkFailure => "The modem is not registered on the network.",
            SmsModemErrorCode.MessagingNetworkError => "The network rejected the message.",
            SmsModemErrorCode.SmsServiceNotSupportedByNetwork => "SMS is not supported on the current network.",
            SmsModemErrorCode.SmsOperationNotSupportedByDevice => "The modem does not support this SMS operation.",
            SmsModemErrorCode.InvalidSmscAddress => "Invalid SMS service center address.",
            SmsModemErrorCode.FixedDialingNumberRestricted => "Blocked by fixed dialing restriction on the SIM.",
            SmsModemErrorCode.MessageNotEncodedProperly => "The message could not be encoded.",
            SmsModemErrorCode.MessageTooLarge => "The message is too long.",
            SmsModemErrorCode.DeviceNotReady => "The modem is not ready, try again shortly.",
            SmsModemErrorCode.DeviceFailure => "The modem reported a failure.",
            // Seen on Hutch: an instant "Other" with no cause when the prepaid balance is too low; the
            // operator then sends a class-0 (flash) SMS explaining it.
            SmsModemErrorCode.Other when result.NetworkCauseCode == 0 =>
                "The network refused the message. This usually means there isn't enough credit — check your balance.",
            _ => $"Send failed ({result.ModemErrorCode}, network cause {result.NetworkCauseCode}).",
        };
        return new SmsSendOutcome(false, why, result.IsErrorTransient);
    }

    /// <summary>Starts listening for incoming SMS. Returns false if the platform refuses.</summary>
    /// <remarks>
    /// Must run on an MTA thread: when the legacy SmsDevice is created on a WPF (STA) UI thread its
    /// SmsMessageReceived event never fires (observed 2026-11-21), while the same code in a console app works.
    /// </remarks>
    public Task<bool> StartListeningAsync() => Task.Run(async () =>
    {
        try
        {
            await EnsureDeviceAsync(TimeSpan.FromSeconds(20));
            _legacy!.SmsMessageReceived += OnLegacyReceived;
            return true;
        }
        catch { return false; }
    });

    private async Task EnsureDeviceAsync(TimeSpan limit)
    {
        if (_legacy is not null) return;
        // create on a thread-pool (MTA) thread so events are delivered
        _legacy = await Task.Run(() => WithTimeout(SmsDevice.GetDefaultAsync().AsTask(), limit, "Opening the SMS device"));
    }

    /// <summary>
    /// Reads messages currently held in the modem/SIM store (bulk listing).
    /// NOTE: on the DW5820e the bulk listing returns PDUs truncated to ~50 bytes of text and sometimes hangs;
    /// it is only a fallback — live messages are fetched individually by id (full PDU) in <see cref="OnLegacyReceived"/>.
    /// </summary>
    public Task<IReadOnlyList<IncomingSms>> ReadStoreAsync(TimeSpan? timeout = null) => Task.Run(() => ReadStoreCoreAsync(timeout));

    private async Task<IReadOnlyList<IncomingSms>> ReadStoreCoreAsync(TimeSpan? timeout)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(20);
        await EnsureDeviceAsync(limit);
        var msgs = await WithTimeout(_legacy!.MessageStore.GetMessagesAsync(SmsMessageFilter.All).AsTask(), limit, "Reading the message store");
        var parts = new List<(uint, SmsPart)>();
        var list = new List<IncomingSms>();
        foreach (var m in msgs)
        {
            if (m is SmsBinaryMessage b)
            {
                try { parts.Add((b.Id, SmsPdu.Decode(b.GetData()))); } catch { /* undecodable */ }
            }
            else if (m is SmsTextMessage t)
                list.Add(new IncomingSms(t.From ?? "", t.Body ?? "", t.Timestamp, t.Id));
        }
        list.AddRange(SmsAssembler.Assemble(parts).Select(a =>
            new IncomingSms(a.Sender, a.Text, a.Timestamp, a.StoreIds.Min()) { PartIds = a.StoreIds }));
        return list;
    }

    /// <summary>Fetches one stored message by id and decodes its PDU (full length, unlike the bulk listing).</summary>
    private async Task<SmsPart?> FetchPartAsync(uint id)
    {
        if (_legacy is null) return null;
        try
        {
            var m = await WithTimeout(_legacy.MessageStore.GetMessageAsync(id).AsTask(), TimeSpan.FromSeconds(15), "Reading a message");
            return m is SmsBinaryMessage b ? SmsPdu.Decode(b.GetData()) : null;
        }
        catch { return null; }
    }

    // parts of multipart messages waiting for their siblings, keyed by sender|ref|total
    private readonly Dictionary<string, Dictionary<int, (uint Id, SmsPart Part)>> _pending = [];
    private readonly SemaphoreSlim _rxGate = new(1, 1);

    private async Task HandleIncomingAsync(uint id, string fallbackFrom, string fallbackBody, DateTimeOffset fallbackTs)
    {
        await _rxGate.WaitAsync();
        try
        {
            var part = await FetchPartAsync(id);
            Diagnostic?.Invoke(part is null ? $"Slot {id}: could not read PDU, using Windows text"
                : $"Slot {id}: part {part.ConcatSeq}/{part.ConcatTotal} ref {part.ConcatRef}{(part.Truncated ? " (truncated)" : "")}");
            if (part is null)
            {
                MessageReceived?.Invoke(this, new IncomingSms(fallbackFrom, fallbackBody, fallbackTs, id));
                return;
            }
            if (!part.IsMultipart)
            {
                MessageReceived?.Invoke(this, new IncomingSms(part.Sender, part.Text, part.Timestamp, id));
                return;
            }

            var key = $"{part.Sender}|{part.ConcatRef}|{part.ConcatTotal}";
            if (!_pending.TryGetValue(key, out var set)) _pending[key] = set = [];
            set[part.ConcatSeq] = (id, part);

            // The legacy API raises one event per concatenated message; fetch sibling parts from nearby ids.
            for (int d = 1; d <= 4 && set.Count < part.ConcatTotal; d++)
            {
                foreach (var probe in new[] { (long)id - d, (long)id + d })
                {
                    if (probe <= 0 || set.Values.Any(v => v.Id == probe)) continue;
                    var p = await FetchPartAsync((uint)probe);
                    if (p is { IsMultipart: true } && p.Sender == part.Sender && p.ConcatRef == part.ConcatRef && p.ConcatTotal == part.ConcatTotal)
                        set[p.ConcatSeq] = ((uint)probe, p);
                }
            }

            if (set.Count >= part.ConcatTotal) Emit(key);
            else _ = EmitLaterAsync(key); // wait for the remaining parts, then emit what we have
        }
        finally { _rxGate.Release(); }
    }

    private async Task EmitLaterAsync(string key)
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        await _rxGate.WaitAsync();
        try { if (_pending.ContainsKey(key)) Emit(key); }
        finally { _rxGate.Release(); }
    }

    private void Emit(string key)
    {
        if (!_pending.Remove(key, out var set)) return;
        var a = SmsAssembler.Assemble(set.Values.Select(v => (v.Id, v.Part))).First();
        MessageReceived?.Invoke(this, new IncomingSms(a.Sender, a.Text, a.Timestamp, a.StoreIds.Min()) { PartIds = a.StoreIds });
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan limit, string what)
    {
        if (await Task.WhenAny(task, Task.Delay(limit)) != task)
            throw new ModemException($"{what} timed out — the modem may be off the network.");
        return await task;
    }

    private void OnLegacyReceived(SmsDevice sender, SmsMessageReceivedEventArgs args)
    {
        // Never touch args.BinaryMessage: it crashes the process with an access violation (legacy API bug).
        Diagnostic?.Invoke("Receive event fired");
        try
        {
            var t = args.TextMessage;
            Diagnostic?.Invoke($"Receive event: slot {t.Id} from {t.From} ({t.PartCount} part(s))");
            _ = HandleIncomingAsync(t.Id, t.From ?? "", t.Body ?? "", t.Timestamp);
        }
        catch (Exception ex) { Diagnostic?.Invoke($"Receive event error: 0x{ex.HResult:X8} {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_legacy is not null) { try { _legacy.SmsMessageReceived -= OnLegacyReceived; } catch { } }
    }
#pragma warning restore CS0618
}

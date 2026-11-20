using Cellular.Core;

// Diagnostic CLI for the modem.
//   cellular status                  modem / SIM / network state
//   cellular caps                    capabilities (voice / SMS / USSD)
//   cellular at <cmd> [<cmd>...]     raw AT via the vendor MBIM AT channel (whitelisted on this firmware)
//   cellular sms-list                messages in the modem/SIM store
//   cellular sms-send <to> <text>    send an SMS
//   cellular ussd <code> [reply...]  run a USSD code, optionally answering menu prompts
if (args.Length == 0)
{
    Console.WriteLine("usage: cellular status | caps | at <cmd>... | sms-list | sms-send <to> <text> | ussd <code> [reply...]");
    return 1;
}

switch (args[0].ToLowerInvariant())
{
    case "status":
        var s = await ModemStatusReader.ReadAsync();
        foreach (var p in typeof(ModemStatus).GetProperties())
            Console.WriteLine($"{p.Name,-20} {p.GetValue(s)}");
        return 0;

    case "cells":
    {
        var id = Windows.Networking.NetworkOperators.MobileBroadbandAccount.AvailableNetworkAccountIds.First();
        var net = Windows.Networking.NetworkOperators.MobileBroadbandAccount.CreateFromNetworkAccountId(id).CurrentNetwork;
        var info = await net.GetCellsInfoAsync();
        void Dump(string title, System.Collections.IEnumerable items)
        {
            int n = 0;
            foreach (var cell in items)
            {
                n++;
                var props = cell.GetType().GetProperties()
                    .Select(p => { try { var v = p.GetValue(cell); return v is null ? null : $"{p.Name}={v}"; } catch { return null; } })
                    .Where(s => s is not null);
                Console.WriteLine($"  [{title}] " + string.Join(" ", props));
            }
            if (n == 0) Console.WriteLine($"  [{title}] none");
        }
        Dump("serving LTE", info.ServingCellsLte);
        Dump("neighbor LTE", info.NeighboringCellsLte);
        Dump("serving UMTS", info.ServingCellsUmts);
        Dump("neighbor UMTS", info.NeighboringCellsUmts);
        Dump("serving GSM", info.ServingCellsGsm);
        Dump("neighbor GSM", info.NeighboringCellsGsm);
        return 0;
    }

    case "regmode":
        Console.WriteLine($"Register mode: {MbnRegistration.GetRegisterMode()?.ToString() ?? "unavailable"}");
        return 0;

    case "register":
    {
        uint dc = args.Length > 1 ? args[1].ToLowerInvariant() switch
        {
            "3g" => MbnRegistration.DataClass3G,
            "lte" or "4g" => MbnRegistration.DataClassLte,
            _ => MbnRegistration.DataClassAny,
        } : MbnRegistration.DataClassAny;
        Console.WriteLine($"Register mode before: {MbnRegistration.GetRegisterMode()}, current data class 0x{MbnRegistration.GetCurrentDataClass():X}");
        Console.WriteLine($"Requesting automatic registration, preferred data class 0x{dc:X}");
        MbnRegistration.RegisterAutomatic(dc);
        RegistrationState last = RegistrationState.Unknown;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000);
            var st = await ModemStatusReader.ReadAsync();
            if (st.Registration != last || i % 5 == 4)
                Console.WriteLine($"  t+{(i + 1) * 2,2}s state={st.Registration} provider={st.ProviderName} type={st.NetworkType} rssi={st.RssiDbm} dc=0x{MbnRegistration.GetCurrentDataClass():X}");
            last = st.Registration;
            if (st.IsRegistered && i >= 4) return 0;
        }
        return 2;
    }

    case "caps":
        var c = await ModemCapabilities.ReadAsync();
        Console.WriteLine(c);
        Console.WriteLine($"HasVoice={c?.HasVoice} SmsSend={c?.CanSendSms} SmsRecv={c?.CanReceiveSms} Ussd={c?.HasUssd}");
        return 0;

    case "at":
        var ch = new AtChannel();
        foreach (var cmd in args.Skip(1))
        {
            try
            {
                var r = await ch.SendAsync(cmd);
                Console.WriteLine($">>> {cmd}\n{r}\n");
            }
            catch (Exception ex) { Console.WriteLine($">>> {cmd}  FAILED: {ex.Message}\n"); }
        }
        return 0;

    case "services":
    {
        var modem = Windows.Networking.NetworkOperators.MobileBroadbandModem.GetDefault();
        foreach (var info in modem.DeviceServices)
        {
            var svc = modem.GetDeviceService(info.DeviceServiceId);
            string cmds = "", dss = "";
            try { cmds = string.Join(",", svc.SupportedCommands); } catch (Exception ex) { cmds = $"err 0x{ex.HResult:X8}"; }
            Console.WriteLine($"{info.DeviceServiceId}  read={info.IsDataReadSupported} write={info.IsDataWriteSupported}  cids=[{cmds}]{dss}");
        }
        return 0;
    }

    case "mbim-query":
    {
        // usage: cellular mbim-query <service-guid> <cid> [hex-payload]
        var modem = Windows.Networking.NetworkOperators.MobileBroadbandModem.GetDefault();
        var svc = modem.GetDeviceService(Guid.Parse(args[1]));
        var session = svc.OpenCommandSession();
        try
        {
            var payload = args.Length > 3 ? Convert.FromHexString(args[3]) : [];
            var r = await session.SendQueryCommandAsync(uint.Parse(args[2]),
                System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(payload));
            var data = r.ResponseData is { Length: > 0 } b ? System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(b) : [];
            Console.WriteLine($"status={r.StatusCode} bytes={data.Length}\n{Convert.ToHexString(data)}");
        }
        catch (Exception ex) { Console.WriteLine($"failed 0x{ex.HResult:X8} {ex.Message}"); }
        finally { session.CloseSession(); }
        return 0;
    }

    case "parse":
    {
        var b = BalanceParser.Parse(string.Join(' ', args.Skip(1)));
        Console.WriteLine($"looksLikeBalance={BalanceParser.LooksLikeBalance(string.Join(' ', args.Skip(1)))}");
        Console.WriteLine($"credit={b.Credit} creditEx={b.CreditExpires:yyyy-MM-dd} points={b.Points} validUntil={b.ValidUntil:yyyy-MM-dd}");
        foreach (var d in b.Data) Console.WriteLine($"  data: {d.Label} = {BalanceParser.FormatData(d.Megabytes)} ex {d.Expires:yyyy-MM-dd}");
        return 0;
    }

    case "sms-listen":
    {
        // usage: cellular sms-listen [seconds] [to text...]  — optionally sends a message, prints raw incoming PDUs
#pragma warning disable CS0618
        var dev = await Windows.Devices.Sms.SmsDevice.GetDefaultAsync();
        // NOTE: e.BinaryMessage crashes the process (AV inside the legacy WinRT API) — only use TextMessage.
        dev.SmsMessageReceived += async (s, e) =>
        {
            try
            {
                var t = e.TextMessage;
                Console.WriteLine($"[event] id={t.Id} from={t.From} parts={t.PartCount} len={t.Body.Length}\n  text={t.Body}");
                var stored = await dev.MessageStore.GetMessageAsync(t.Id);
                if (stored is Windows.Devices.Sms.SmsBinaryMessage b)
                {
                    var pdu = b.GetData();
                    Console.WriteLine($"  store pdu ({pdu.Reverse().SkipWhile(x => x == 0).Count()} non-padding bytes)={Convert.ToHexString(pdu)}");
                    var p = SmsPdu.Decode(pdu);
                    Console.WriteLine($"  decoded part={p.ConcatSeq}/{p.ConcatTotal}: {p.Text}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[event] error {ex.Message}"); }
        };
#pragma warning restore CS0618
        int secs = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 60;
        if (args.Length > 3)
        {
            using var sms = new SmsService();
            Console.WriteLine(await sms.SendAsync(args[2], string.Join(' ', args.Skip(3))));
        }
        await Task.Delay(TimeSpan.FromSeconds(secs));
        return 0;
    }

    case "sms-raw":
    {
#pragma warning disable CS0618
        var dev = await Windows.Devices.Sms.SmsDevice.GetDefaultAsync();
        var listed = await dev.MessageStore.GetMessagesAsync(Windows.Devices.Sms.SmsMessageFilter.All);
        IEnumerable<Windows.Devices.Sms.ISmsMessage> msgs = listed;
        if (args.Contains("--byid"))
        {
            var byId = new List<Windows.Devices.Sms.ISmsMessage>();
            foreach (var l in listed) byId.Add(await dev.MessageStore.GetMessageAsync(l.Id));
            msgs = byId;
        }
        foreach (var m in msgs)
        {
            Console.Write($"[{m.Id}] {m.GetType().Name} class={m.MessageClass} ");
            if (m is Windows.Devices.Sms.SmsBinaryMessage b)
            {
                var pdu = b.GetData();
                try
                {
                    var p = SmsPdu.Decode(pdu);
                    Console.WriteLine($"from={p.Sender} at={p.Timestamp:yyyy-MM-dd HH:mm:ss zzz} class={p.MessageClass} part={p.ConcatSeq}/{p.ConcatTotal} ref={p.ConcatRef}\n      {p.Text}");
                }
                catch (Exception ex) { Console.WriteLine($"decode failed: {ex.Message} pdu={Convert.ToHexString(pdu)}"); }
            }
            else if (m is Windows.Devices.Sms.SmsTextMessage t)
                Console.WriteLine($"text from={t.From} parts={t.PartCount} body={t.Body}");
            else Console.WriteLine();
        }
#pragma warning restore CS0618
        return 0;
    }

    case "sms-list":
        using (var sms = new SmsService())
        {
            var list = await sms.ReadStoreAsync();
            Console.WriteLine($"{list.Count} message(s) in store");
            foreach (var m in list) Console.WriteLine($"[{m.StoreId}] {m.Timestamp:yyyy-MM-dd HH:mm} {m.From}: {m.Body}");
        }
        return 0;

    case "sms-send" when args.Length >= 3:
        using (var sms = new SmsService())
        {
            var o = await sms.SendAsync(args[1], string.Join(' ', args.Skip(2)));
            Console.WriteLine(o);
            return o.Success ? 0 : 2;
        }

    case "ussd" when args.Length >= 2:
        var u = new UssdService();
        foreach (var step in args.Skip(1))
        {
            var r = await u.SendAsync(step);
            Console.WriteLine($">>> {step}  [{r.State}] {r.Error}\n{r.Text}\n");
            if (r.State != UssdState.AwaitingReply) break;
        }
        await u.EndAsync();
        return 0;

    default:
        Console.WriteLine("unknown command");
        return 1;
}

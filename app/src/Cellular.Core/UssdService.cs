using Windows.Networking.NetworkOperators;

namespace Cellular.Core;

public enum UssdState { Ended, AwaitingReply, Error }

public sealed record UssdResult(UssdState State, string Text, string? Error = null);

/// <summary>
/// USSD over the MBIM USSD service. A session stays open while the network expects a reply
/// (menu navigation); call <see cref="EndAsync"/> to cancel it.
/// </summary>
public sealed class UssdService
{
    private UssdSession? _session;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public bool InSession => _session is not null;

    public async Task<UssdResult> SendAsync(string text, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var accountId = MobileBroadbandAccount.AvailableNetworkAccountIds.FirstOrDefault()
                ?? throw new ModemException("No mobile broadband account found.");
            _session ??= UssdSession.CreateFromNetworkAccountId(accountId);

            var op = _session.SendMessageAndGetReplyAsync(new UssdMessage(text.Trim())).AsTask(ct);
            var done = await Task.WhenAny(op, Task.Delay(TimeSpan.FromSeconds(45), ct));
            if (done != op)
            {
                Reset();
                return new UssdResult(UssdState.Error, "", "The network did not answer in time.");
            }
            var reply = await op;
            var body = reply.Message?.PayloadAsText ?? "";

            switch (reply.ResultCode)
            {
                case UssdResultCode.ActionRequired:
                    return new UssdResult(UssdState.AwaitingReply, body);
                case UssdResultCode.NoActionRequired:
                    Reset();
                    return new UssdResult(UssdState.Ended, body);
                case UssdResultCode.Terminated:
                    Reset();
                    return new UssdResult(UssdState.Ended, body, string.IsNullOrEmpty(body) ? "The network ended the session." : null);
                case UssdResultCode.OtherLocalClient:
                    Reset();
                    return new UssdResult(UssdState.Error, body, "Another app on this PC is using USSD.");
                case UssdResultCode.OperationNotSupported:
                    Reset();
                    return new UssdResult(UssdState.Error, body, "The network or modem does not support this request.");
                case UssdResultCode.NetworkTimeout:
                    Reset();
                    return new UssdResult(UssdState.Error, body, "Network timed out.");
                default:
                    Reset();
                    return new UssdResult(UssdState.Error, body, $"Unexpected result: {reply.ResultCode}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Reset();
            var msg = ex.HResult switch
            {
                unchecked((int)0x800710DF) => "The modem is not ready for USSD (not registered, or USSD unavailable on the current network).",
                _ => ex.Message,
            };
            return new UssdResult(UssdState.Error, "", msg);
        }
        finally { Gate.Release(); }
    }

    public Task EndAsync()
    {
        Reset();
        return Task.CompletedTask;
    }

    private void Reset()
    {
        try { _session?.Close(); } catch { /* ignore */ }
        _session = null;
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Cellular.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CellularStudio.ViewModels;

public sealed partial class MainViewModel
{
    private readonly SmsService _sms = new();
    private ICollectionView? _conversationView;

    public ObservableCollection<Conversation> Conversations { get; } = [];

    public ICollectionView ConversationView
    {
        get
        {
            if (_conversationView is null)
            {
                _conversationView = CollectionViewSource.GetDefaultView(Conversations);
                _conversationView.SortDescriptions.Add(new SortDescription(nameof(Conversation.LastTime), ListSortDirection.Descending));
                if (_conversationView is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
                {
                    live.LiveSortingProperties.Add(nameof(Conversation.LastTime));
                    live.IsLiveSorting = true;
                }
                _conversationView.Filter = o => o is Conversation c && (string.IsNullOrWhiteSpace(SearchText)
                    || c.Address.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                    || c.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                    || c.Messages.Any(m => m.Body.Contains(SearchText, StringComparison.OrdinalIgnoreCase)));
            }
            return _conversationView;
        }
    }

    [ObservableProperty] private string _searchText = "";
    partial void OnSearchTextChanged(string value) => ConversationView.Refresh();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThread), nameof(ThreadTitle), nameof(ThreadSubtitle), nameof(CanReply))]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private Conversation? _selectedConversation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThread), nameof(ThreadTitle), nameof(ThreadSubtitle), nameof(CanReply))]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _isComposingNew;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _newRecipient = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ComposeCounter))]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _composeText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _isSending;

    [ObservableProperty] private bool _smsListening;

    public bool HasThread => IsComposingNew || SelectedConversation is not null;
    public bool CanReply => IsComposingNew || SelectedConversation is { IsService: false };
    public string ThreadTitle => IsComposingNew ? "New message" : SelectedConversation?.DisplayName ?? "";
    public string ThreadSubtitle => IsComposingNew ? "Enter a phone number to start a conversation"
        : SelectedConversation is { IsService: true } ? "Service sender · replies not supported"
        : SelectedConversation is { } c ? $"{c.Messages.Count} message{(c.Messages.Count == 1 ? "" : "s")} · SMS" : "";

    public int UnreadTotal => Conversations.Sum(c => c.Unread);

    public string ComposeCounter
    {
        get
        {
            if (string.IsNullOrEmpty(ComposeText)) return "";
            var (segs, rem, enc) = SmsService.Measure(ComposeText);
            return $"{rem} left · {segs} SMS{(enc == "Unicode" ? " · Unicode" : "")}";
        }
    }

    partial void OnSelectedConversationChanged(Conversation? value)
    {
        if (value is null) return;
        IsComposingNew = false;
        if (value.Unread > 0)
        {
            value.Unread = 0;
            foreach (var m in value.Messages) m.Model.Read = true;
            OnPropertyChanged(nameof(UnreadTotal));
            Save();
        }
    }

    [RelayCommand]
    private void NewMessage()
    {
        CurrentPage = "Messages";
        SelectedConversation = null;
        IsComposingNew = true;
        NewRecipient = "";
    }

    [RelayCommand]
    private void CancelNewMessage()
    {
        IsComposingNew = false;
        SelectedConversation = ConversationView.Cast<Conversation>().FirstOrDefault();
    }

    private bool CanSendMessage() =>
        !IsSending && !string.IsNullOrWhiteSpace(ComposeText) &&
        (IsComposingNew ? IsValidNumber(NewRecipient) : SelectedConversation is { IsService: false });

    private static bool IsValidNumber(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return false;
        var digits = t.Count(char.IsDigit);
        return digits >= 3 && t.All(ch => char.IsDigit(ch) || ch is '+' or ' ' or '-');
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessage()
    {
        var to = IsComposingNew ? NewRecipient.Trim().Replace(" ", "").Replace("-", "") : SelectedConversation!.Address;
        var body = ComposeText.TrimEnd();

        var model = new StoredMessage
        {
            Address = to, Body = body, Direction = MessageDirection.Outgoing,
            Status = MessageStatus.Sending, Timestamp = DateTimeOffset.Now, Read = true,
        };
        var item = new MessageItem(model);
        var conv = GetOrCreateConversation(to);
        conv.Messages.Add(item);
        conv.Touch();
        _data.Messages.Add(model);
        IsComposingNew = false;
        SelectedConversation = conv;
        ComposeText = "";
        RefreshThreadSubtitle();

        await DeliverAsync(item);
    }

    [RelayCommand]
    private async Task Retry(MessageItem item)
    {
        if (item.Status != MessageStatus.Failed) return;
        item.Status = MessageStatus.Sending;
        await DeliverAsync(item);
    }

    private async Task DeliverAsync(MessageItem item)
    {
        IsSending = true;
        try
        {
            if (!Status.IsRegistered)
            {
                item.Error = "No network";
                item.Status = MessageStatus.Failed;
                ShowToast(ToastKind.Error, "Message not sent", "The modem isn't registered on a network.");
                Log("SMS", $"Not sent to {item.Model.Address}: no network");
                return;
            }
            var result = await _sms.SendAsync(item.Model.Address, item.Body);
            if (result.Success)
            {
                item.Error = null;
                item.Status = MessageStatus.Sent;
                Log("SMS", $"Sent to {item.Model.Address} ({item.Body.Length} chars)");
            }
            else
            {
                item.Error = result.Error;
                item.Status = MessageStatus.Failed;
                ShowToast(ToastKind.Error, "Message not sent", result.Error);
                Log("SMS", $"Failed to {item.Model.Address}: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            item.Error = ex.Message;
            item.Status = MessageStatus.Failed;
            ShowToast(ToastKind.Error, "Message not sent", ex.Message);
            Log("SMS", $"Error: {ex.Message}");
        }
        finally
        {
            IsSending = false;
            Save();
        }
    }

    [RelayCommand]
    private void DeleteConversation(Conversation? c)
    {
        if (c is null) return;
        _data.Messages.RemoveAll(m => PhoneFormat.Key(m.Address) == PhoneFormat.Key(c.Address));
        Conversations.Remove(c);
        if (SelectedConversation == c) SelectedConversation = null;
        Save();
        ShowToast(ToastKind.Info, "Conversation deleted", "Removed from this app (the SIM copy is not affected).");
    }

    private Conversation GetOrCreateConversation(string address)
    {
        var key = PhoneFormat.Key(address);
        var c = Conversations.FirstOrDefault(x => PhoneFormat.Key(x.Address) == key);
        if (c is null)
        {
            c = new Conversation(address);
            Conversations.Add(c);
        }
        return c;
    }

    /// <summary>Higher = more complete text (truncated/padded store copies score lower).</summary>
    private static int TextQuality(string body)
    {
        var t = body.TrimEnd('@', '…');
        int penalty = body.Contains("@@") ? 1000 : 0;
        return t.Length - penalty - (body.EndsWith('…') ? 5 : 0);
    }

    private void LoadMessages()
    {
        // Messages imported by earlier versions used Windows' decoder, which pads with "@@@@": clean them up.
        foreach (var m in _data.Messages.Where(m => m.Direction == MessageDirection.Incoming && m.Body.Contains("@@")))
            m.Body = m.Body.TrimEnd('@').TrimEnd() + "…";

        foreach (var m in _data.Messages.OrderBy(m => m.Timestamp))
        {
            if (m.Status == MessageStatus.Sending) { m.Status = MessageStatus.Failed; m.Error = "Interrupted"; }
            var c = GetOrCreateConversation(m.Address);
            c.Messages.Add(new MessageItem(m));
            if (m.Direction == MessageDirection.Incoming && !m.Read) c.Unread++;
        }
        foreach (var c in Conversations) c.Touch();
    }

    private void RefreshThreadSubtitle() => OnPropertyChanged(nameof(ThreadSubtitle));

    private async Task StartSmsAsync()
    {
        _sms.MessageReceived += (_, msg) => OnUi(() => AddIncoming(msg, notify: true));
        _sms.Diagnostic += m => Log("SMS", m);
        SmsListening = await _sms.StartListeningAsync();
        Log("SMS", SmsListening ? "Listening for incoming messages" : "Incoming SMS listener unavailable");
        await SyncStoreAsync(notify: false);
        _smsStarted = true;
        MaybeAutoRefreshBalance();
    }

    private bool _smsStarted;

    [RelayCommand]
    private async Task SyncStore() => await SyncStoreAsync(notify: true);

    private bool _syncing;
    private DateTimeOffset _lastPoll = DateTimeOffset.Now;
    private static readonly TimeSpan InboxPollInterval = TimeSpan.FromMinutes(2);

    /// <param name="notify">Show a summary toast (manual refresh).</param>
    /// <param name="announceNew">Treat newly found messages as new arrivals (toast + tray notification).</param>
    /// <param name="quiet">Don't log when nothing changed (background polling).</param>
    private async Task SyncStoreAsync(bool notify, bool announceNew = false, bool quiet = false)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var msgs = await _sms.ReadStoreAsync();
            int added = msgs.OrderBy(m => m.Timestamp).Count(m => AddIncoming(m, notify: announceNew));
            if (!quiet || added > 0) Log("SMS", $"Message store: {msgs.Count} message(s), {added} new");
            if (notify) ShowToast(ToastKind.Info, added > 0 ? $"{added} new message(s)" : "Inbox is up to date");
        }
        catch (Exception ex)
        {
            if (!quiet) Log("SMS", $"Could not read message store: {ex.Message}");
            if (notify) ShowToast(ToastKind.Warning, "Couldn't read messages", ex.Message);
        }
        finally { _syncing = false; _lastPoll = DateTimeOffset.Now; }
    }

    /// <summary>
    /// The legacy receive event doesn't fire inside this WPF process (it does in a console app), so the
    /// modem store is polled as the reliable path for incoming SMS.
    /// </summary>
    private void MaybePollInbox()
    {
        if (!_smsStarted || !Status.IsRegistered || _syncing) return;
        if (DateTimeOffset.Now - _lastPoll < InboxPollInterval) return;
        _ = SyncStoreAsync(notify: false, announceNew: true, quiet: true);
    }

    /// <summary>Adds an incoming SMS unless it is already known. Returns true if added.</summary>
    private bool AddIncoming(IncomingSms msg, bool notify)
    {
        // Older versions stored each part of a multipart SMS separately; drop those fragments now that
        // the reassembled message (which carries all part ids) has arrived.
        if (msg.PartIds.Count > 1)
        {
            var fragments = _data.Messages.Where(m => m.Direction == MessageDirection.Incoming && m.StoreId is uint sid &&
                    sid != msg.StoreId && msg.PartIds.Contains(sid) &&
                    PhoneFormat.Key(m.Address) == PhoneFormat.Key(msg.From) &&
                    Math.Abs((m.Timestamp - msg.Timestamp).TotalMinutes) < 10).ToList();
            foreach (var f in fragments)
            {
                _data.Messages.Remove(f);
                var conv = GetOrCreateConversation(f.Address);
                var item = conv.Messages.FirstOrDefault(x => x.Model == f);
                if (item is not null) conv.Messages.Remove(item);
                conv.Touch();
            }
            if (fragments.Count > 0) Save();
        }

        // Same stored message seen again (bulk listing = truncated, live fetch = full): keep the better text.
        // Store slots are reused by the modem and operators send identical texts (balance replies), so a
        // message is "the same" only if it occupies the same slot with the same network timestamp (±1 min).
        var existing = _data.Messages.FirstOrDefault(m => m.Direction == MessageDirection.Incoming &&
            PhoneFormat.Key(m.Address) == PhoneFormat.Key(msg.From) &&
            (msg.StoreId != 0
                ? m.StoreId == msg.StoreId && Math.Abs((m.Timestamp - msg.Timestamp).TotalMinutes) < 1
                : m.Body == msg.Body && Math.Abs((m.Timestamp - msg.Timestamp).TotalSeconds) < 30));
        if (existing is not null)
        {
            if (TextQuality(msg.Body) > TextQuality(existing.Body))
            {
                existing.Body = msg.Body;
                var conv = GetOrCreateConversation(existing.Address);
                var idx = conv.Messages.ToList().FindIndex(x => x.Model == existing);
                if (idx >= 0) conv.Messages[idx] = new MessageItem(existing);
                conv.Touch();
                Save();
                // a fuller copy of a known message may complete a balance statement, but never counts as "fresh"
                TryCaptureBalanceFromSms(msg.From, existing.Body, existing.Timestamp, notify: false, allowAsReply: false);
            }
            return false;
        }

        var model = new StoredMessage
        {
            Address = msg.From, Body = msg.Body, Timestamp = msg.Timestamp == default ? DateTimeOffset.Now : msg.Timestamp,
            Direction = MessageDirection.Incoming, Status = MessageStatus.Received, StoreId = msg.StoreId,
        };
        _data.Messages.Add(model);
        var c = GetOrCreateConversation(msg.From);
        var insertAt = c.Messages.Count;
        while (insertAt > 0 && c.Messages[insertAt - 1].Timestamp > model.Timestamp) insertAt--;
        c.Messages.Insert(insertAt, new MessageItem(model));
        c.Touch();

        if (SelectedConversation == c && CurrentPage == "Messages") model.Read = true;
        else c.Unread++;
        OnPropertyChanged(nameof(UnreadTotal));
        Save();

        // Live messages: use the local receive time (the operator's SMSC clock can be minutes off).
        bool wasPending = _balanceRequestedAt is not null;
        bool isBalanceReply = TryCaptureBalanceFromSms(msg.From, msg.Body, notify ? DateTimeOffset.Now : model.Timestamp, notify,
            allowAsReply: notify);
        if (isBalanceReply && wasPending)
        {
            model.Read = true;                     // the balance toast is enough; don't count it as unread
            if (c.Unread > 0) c.Unread--;
            OnPropertyChanged(nameof(UnreadTotal));
            Save();
            notify = false;
        }

        if (notify)
        {
            Log("SMS", $"Received from {msg.From}");
            ShowToast(ToastKind.Info, $"New message from {PhoneFormat.Pretty(msg.From)}",
                msg.Body.Length > 90 ? msg.Body[..90] + "…" : msg.Body);
            IncomingMessage?.Invoke(msg.From, msg.Body, c);
        }
        return true;
    }
}

using System.Collections.ObjectModel;
using Cellular.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CellularStudio.ViewModels;

public enum ToastKind { Info, Success, Warning, Error }

public sealed partial class Toast : ObservableObject
{
    public required string Title { get; init; }
    public string? Message { get; init; }
    public ToastKind Kind { get; init; }
    public string Glyph => Kind switch
    {
        ToastKind.Success => "",
        ToastKind.Warning => "",
        ToastKind.Error => "",
        _ => "",
    };
}

public sealed record LogEntry(DateTimeOffset Time, string Category, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

public sealed partial class MessageItem : ObservableObject
{
    public MessageItem(StoredMessage model) { Model = model; }
    public StoredMessage Model { get; }
    public string Body => Model.Body;
    public DateTimeOffset Timestamp => Model.Timestamp;
    public bool IsOutgoing => Model.Direction == MessageDirection.Outgoing;

    public MessageStatus Status
    {
        get => Model.Status;
        set { Model.Status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsFailed)); }
    }

    public string? Error
    {
        get => Model.Error;
        set { Model.Error = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public bool IsFailed => Model.Status == MessageStatus.Failed;

    public string StatusText => Model.Status switch
    {
        MessageStatus.Sending => "Sending…",
        MessageStatus.Sent => $"Sent · {Model.Timestamp.ToLocalTime():HH:mm}",
        MessageStatus.Failed => $"Not sent · {Model.Error}",
        _ => Model.Timestamp.ToLocalTime().ToString("HH:mm"),
    };
}

public sealed partial class Conversation : ObservableObject
{
    public Conversation(string address) { Address = address; }

    public string Address { get; }
    public ObservableCollection<MessageItem> Messages { get; } = [];

    /// <summary>Alphanumeric sender ids (e.g. "Hutch") are operator/service messages that can't be replied to.</summary>
    public bool IsService => Address.Any(char.IsLetter) || Address.Length <= 6;

    public string DisplayName => PhoneFormat.Pretty(Address);

    public string Initials
    {
        get
        {
            if (Address.Any(char.IsLetter))
                return new string(Address.Where(char.IsLetter).Take(2).ToArray()).ToUpperInvariant();
            return "#";
        }
    }

    [ObservableProperty] private int _unread;

    public MessageItem? Last => Messages.Count > 0 ? Messages[^1] : null;
    public string Preview => Last is null ? "" : (Last.IsOutgoing ? "You: " : "") + Last.Body.ReplaceLineEndings(" ");
    public DateTimeOffset LastTime => Last?.Timestamp ?? DateTimeOffset.MinValue;

    public void Touch()
    {
        OnPropertyChanged(nameof(Last));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(LastTime));
    }
}

public sealed record UssdBubble(string Text, bool FromUser, DateTimeOffset Time, bool IsError = false, bool IsSystem = false);

public sealed record MenuOption(string Key, string Label);

public sealed record QuickCode(string Code, string Label, string Glyph);

public sealed record DataPackageRow(string Name, string Amount, string ExpiryDate, string ExpiryPhrase, bool ExpiringSoon, double Share)
{
    /// <summary>Bar width as a fraction, with a visible minimum for tiny buckets.</summary>
    public double BarShare => Math.Max(0.02, Share);
}

public sealed record DetailRow(string Label, string Value, bool Copyable = false);

public sealed record CapabilityRow(string Name, bool Supported, string Detail);

public static class PhoneFormat
{
    /// <summary>Formats Sri Lankan numbers as 071 156 8721 / +94 71 156 8721; leaves others alone.</summary>
    public static string Pretty(string a)
    {
        var d = new string(a.Where(char.IsDigit).ToArray());
        if (a.Any(char.IsLetter)) return a;
        if (d.Length == 10 && d.StartsWith('0')) return $"{d[..3]} {d[3..6]} {d[6..]}";
        if (d.Length == 11 && d.StartsWith("94")) return $"+94 {d[2..4]} {d[4..7]} {d[7..]}";
        return a;
    }

    /// <summary>Key used to group a conversation: 0712345678 and +94712345678 are the same person.</summary>
    public static string Key(string a)
    {
        if (a.Any(char.IsLetter)) return a.Trim().ToLowerInvariant();
        var d = new string(a.Where(char.IsDigit).ToArray());
        if (d.StartsWith("94") && d.Length == 11) d = "0" + d[2..];
        return d;
    }
}

using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CellularStudio.ViewModels;

namespace CellularStudio.Views;

public partial class MessagesView : UserControl
{
    private INotifyCollectionChanged? _watched;

    public MessagesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged o) o.PropertyChanged -= OnVm;
            if (e.NewValue is INotifyPropertyChanged n) n.PropertyChanged += OnVm;
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) FocusComposer(); };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedConversation):
                WatchThread();
                ScrollToEnd();
                FocusComposer();
                break;
            case nameof(MainViewModel.IsComposingNew) when Vm?.IsComposingNew == true:
                Dispatcher.BeginInvoke(() => RecipientBox.Focus(), DispatcherPriority.Input);
                break;
        }
    }

    private void WatchThread()
    {
        if (_watched is not null) _watched.CollectionChanged -= OnThreadChanged;
        _watched = Vm?.SelectedConversation?.Messages;
        if (_watched is not null) _watched.CollectionChanged += OnThreadChanged;
    }

    private void OnThreadChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    private void ScrollToEnd() =>
        Dispatcher.BeginInvoke(() => ThreadScroll.ScrollToEnd(), DispatcherPriority.Background);

    private void FocusComposer()
    {
        if (Vm is { IsComposingNew: false, CanReply: true })
            Dispatcher.BeginInvoke(() => ComposeBox.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Enter sends, Shift+Enter inserts a new line.</summary>
    private void ComposeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var tb = ComposeBox;
            var i = tb.CaretIndex;
            tb.Text = tb.Text.Insert(i, Environment.NewLine);
            tb.CaretIndex = i + Environment.NewLine.Length;
        }
        else if (Vm?.SendMessageCommand.CanExecute(null) == true)
        {
            Vm.SendMessageCommand.Execute(null);
        }
        e.Handled = true;
    }
}

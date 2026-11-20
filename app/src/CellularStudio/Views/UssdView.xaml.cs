using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CellularStudio.ViewModels;

namespace CellularStudio.Views;

public partial class UssdView : UserControl
{
    public UssdView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel o)
            {
                o.UssdTranscript.CollectionChanged -= OnTranscript;
                o.PropertyChanged -= OnVm;
            }
            if (e.NewValue is MainViewModel n)
            {
                n.UssdTranscript.CollectionChanged += OnTranscript;
                n.PropertyChanged += OnVm;
            }
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) FocusInput(); };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnTranscript(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => TranscriptScroll.ScrollToEnd(), DispatcherPriority.Background);

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.UssdBusy) && Vm?.UssdBusy == false) FocusInput();
    }

    private void FocusInput() => Dispatcher.BeginInvoke(() => UssdBox.Focus(), DispatcherPriority.Input);

    private void UssdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm?.SendUssdCommand.CanExecute(null) == true)
        {
            Vm.SendUssdCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Vm?.UssdAwaitingReply == true)
        {
            Vm.EndUssdCommand.Execute(null);
            e.Handled = true;
        }
    }
}

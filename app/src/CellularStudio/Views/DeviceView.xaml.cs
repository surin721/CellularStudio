using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CellularStudio.ViewModels;

namespace CellularStudio.Views;

public partial class DeviceView : UserControl
{
    public DeviceView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel o) { o.PropertyChanged -= OnVm; o.CarrierSetupRequested -= OnSetupRequested; }
            if (e.NewValue is MainViewModel n) { n.PropertyChanged += OnVm; n.CarrierSetupRequested += OnSetupRequested; }
            UpdatePill();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CarrierMethodKind)) UpdatePill();
    }

    /// <summary>Colours the balance-method pill: green = verified, blue = custom, amber = not set up.</summary>
    private void UpdatePill()
    {
        if (Vm is null) return;
        var (fg, bg) = Vm.CarrierMethodKind switch
        {
            "ok" => ("Success", "SuccessSoft"),
            "custom" => ("Accent", "AccentSoft"),
            _ => ("Warning", "WarningSoft"),
        };
        MethodPillText.Foreground = (Brush)FindResource(fg);
        MethodPill.Background = (Brush)FindResource(bg);
    }

    /// <summary>"Check balance" without a configured method lands here: scroll to the card and focus the number box.</summary>
    private void OnSetupRequested() =>
        Dispatcher.BeginInvoke(() =>
        {
            CarrierCard.BringIntoView();
            CarrierNumberBox.Focus();
        }, DispatcherPriority.Loaded);
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CellularStudio.ViewModels;

namespace CellularStudio.Views;

public partial class OverviewView : UserControl
{
    public OverviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged o) o.PropertyChanged -= OnVm;
            if (e.NewValue is INotifyPropertyChanged n) n.PropertyChanged += OnVm;
            UpdatePill();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnVm(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.StatusKind)) UpdatePill();
    }

    private void UpdatePill()
    {
        if (Vm is null) return;
        var (fg, bg) = Vm.StatusKind switch
        {
            "ok" => ("Success", "SuccessSoft"),
            "warn" => ("Warning", "WarningSoft"),
            _ => ("Danger", "DangerSoft"),
        };
        PillDot.Fill = (Brush)FindResource(fg);
        PillText.Foreground = (Brush)FindResource(fg);
        StatusPill.Background = (Brush)FindResource(bg);
    }

    private void OpenConversation_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null || (sender as FrameworkElement)?.Tag is not Conversation c) return;
        Vm.CurrentPage = "Messages";
        Vm.SelectedConversation = c;
    }
}

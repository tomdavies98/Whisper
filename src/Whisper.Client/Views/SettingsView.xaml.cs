using System.Windows;
using System.Windows.Controls;
using Whisper.Client.ViewModels;

namespace Whisper.Client.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings)
        {
            await settings.StartMeterAsync();
        }
    }
}

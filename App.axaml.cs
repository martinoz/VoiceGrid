using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VoiceGrid.ViewModels;
using VoiceGrid.Views;

namespace VoiceGrid;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var cs = new CultureInfo("cs-CZ");
        CultureInfo.DefaultThreadCurrentCulture = cs;
        CultureInfo.DefaultThreadCurrentUICulture = cs;
        CultureInfo.CurrentCulture = cs;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel() };

        base.OnFrameworkInitializationCompleted();
    }
}

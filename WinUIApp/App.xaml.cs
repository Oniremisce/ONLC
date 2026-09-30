using Microsoft.UI.Xaml;
using LyricsApp.WinUI.Views;

namespace LyricsApp.WinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (s, e) =>
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "lyricsapp_crash.log"),
                "XAML Unhandled: " + e.Message + "\r\n" + e.Exception); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "lyricsapp_crash.log"),
                "AppDomain: " + e.ExceptionObject); } catch { }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "lyricsapp_crash.log"), "OnLaunched: " + ex); } catch { }
            throw;
        }
    }
}

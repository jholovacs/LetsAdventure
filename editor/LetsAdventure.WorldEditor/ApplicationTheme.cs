using System.Windows;

namespace LetsAdventure.WorldEditor;

/// <summary>Merges light/dark resource dictionaries based on Windows app theme.</summary>
internal static class ApplicationTheme
{
    private const string LightSource = "pack://application:,,,/Themes/Light.xaml";
    private const string DarkSource = "pack://application:,,,/Themes/Dark.xaml";

    public static void Install(Application app)
    {
        var dark = OsTheme.IsAppsDarkMode();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(dark ? DarkSource : LightSource, UriKind.Absolute)
        });

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window w && OsTheme.IsAppsDarkMode())
            OsTheme.TryApplyImmersiveDarkTitleBar(w);
    }
}

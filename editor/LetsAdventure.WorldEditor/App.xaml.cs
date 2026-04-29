using System.Windows;

namespace LetsAdventure.WorldEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ApplicationTheme.Install(this);
        base.OnStartup(e);
    }
}

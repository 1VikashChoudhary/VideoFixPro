using System.Windows;

namespace VideoFixPro;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ProcessGuard.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ProcessGuard.CleanUp();
        base.OnExit(e);
    }
}

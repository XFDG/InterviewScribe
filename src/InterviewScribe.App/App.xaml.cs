using System.Threading;
using System.Windows;

namespace InterviewScribe.App;

public partial class App : Application
{
    private const string SingleInstanceName = @"Local\MediaScribe.SingleInstance";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "MediaScribe 已经在运行。请切换到现有窗口继续操作。",
                "MediaScribe",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex was not owned (for example, startup aborted). It is still safe to dispose.
            }

            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}

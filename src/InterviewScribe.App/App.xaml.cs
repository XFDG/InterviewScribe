using System.Threading;
using System.Windows;

namespace InterviewScribe.App;

public partial class App : Application
{
    private const string SingleInstanceName = @"Local\InterviewScribe.SingleInstance";
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "面试转写助手已经在运行。请切换到现有窗口继续操作。",
                "面试转写助手",
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

using System.Windows;
using System.Windows.Threading;
using LocaltsAccountManager.App.Services;
using LocaltsAccountManager.Infrastructure;
using LocaltsAccountManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LocaltsAccountManager.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal("Unexpected error", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Nothing is left to handle it, but the process should not die over a dropped task.
            args.SetObserved();
        };

        // OnStartup used to be async void: anything thrown before the first await bypassed the
        // dispatcher handler entirely, so a bad config file or an unwritable database closed the
        // app with no window and no message.
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            var services = new ServiceCollection();
            services.AddLocaltsAccountManagerInfrastructure();
            services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
            services.AddSingleton<IUiDialogs, WpfUiDialogs>();
            services.AddSingleton<IClipboardService, WpfClipboardService>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<DonutViewModel>();
            services.AddSingleton<MainWindow>();
            Services = services.BuildServiceProvider();

            await DependencyInjection.InitializeInfrastructureAsync(Services).ConfigureAwait(true);

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            ReportFatal("Localts Account Manager could not start", ex);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        MessageBox.Show(
            args.Exception.Message,
            "Unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        args.Handled = true;
    }

    private static void ReportFatal(string caption, Exception? error)
    {
        var detail = error is null ? "Unknown error." : $"{error.GetType().Name}: {error.Message}";
        MessageBox.Show(detail, caption, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

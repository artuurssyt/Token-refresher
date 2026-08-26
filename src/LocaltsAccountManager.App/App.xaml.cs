using System.Windows;
using LocaltsAccountManager.App.ViewModels;
using LocaltsAccountManager.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LocaltsAccountManager.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.Message,
                "Unexpected error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        var services = new ServiceCollection();
        services.AddLocaltsAccountManagerInfrastructure();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DonutViewModel>();
        services.AddSingleton<MainWindow>();
        Services = services.BuildServiceProvider();

        await DependencyInjection.InitializeInfrastructureAsync(Services).ConfigureAwait(true);

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }
}

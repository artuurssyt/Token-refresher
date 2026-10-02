using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using LocaltsAccountManager.Desktop.Services;
using LocaltsAccountManager.Desktop.Views;
using LocaltsAccountManager.Infrastructure;
using LocaltsAccountManager.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LocaltsAccountManager.Desktop;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            _ = StartAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var dialogs = new AvaloniaUiDialogs();
            var dispatcher = new AvaloniaUiDispatcher();
            var clipboard = new AvaloniaClipboardService();

            var services = new ServiceCollection();
            services.AddLocaltsAccountManagerInfrastructure();
            services.AddSingleton<IUiDispatcher>(dispatcher);
            services.AddSingleton<IUiDialogs>(dialogs);
            services.AddSingleton<IClipboardService>(clipboard);
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<DonutViewModel>();
            Services = services.BuildServiceProvider();

            await DependencyInjection.InitializeInfrastructureAsync(Services).ConfigureAwait(true);

            var window = new MainWindow(Services.GetRequiredService<MainViewModel>());
            dialogs.Host = window;
            clipboard.Host = window;
            desktop.MainWindow = window;
            Console.WriteLine("LOCALTS_WINDOW_SHOWN");
            Console.Out.Flush();

            if (Program.SmokeMode)
            {
                window.Opened += async (_, _) =>
                {
                    Console.WriteLine("LOCALTS_SMOKE_OPENED");
                    Console.Out.Flush();
                    await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                    TryWriteSmokeScreenshot(window);
                    await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
                    Console.WriteLine("LOCALTS_SMOKE_OK");
                    Console.Out.Flush();
                    desktop.Shutdown(0);
                };
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Console.Error.Flush();
            try
            {
                await MessageDialog.ShowAsync(
                    desktop.MainWindow,
                    "Localts Account Manager could not start",
                    $"{ex.GetType().Name}: {ex.Message}",
                    MessageDialogKind.Error).ConfigureAwait(true);
            }
            catch
            {
                // Last-resort: we already wrote stderr.
            }

            desktop.Shutdown(1);
        }
    }

    private static void TryWriteSmokeScreenshot(Views.MainWindow window)
    {
        try
        {
            var dest = Environment.GetEnvironmentVariable("LOCALTS_SMOKE_SCREENSHOT");
            if (string.IsNullOrWhiteSpace(dest))
            {
                dest = Path.Combine(Path.GetTempPath(), "localts-smoke.png");
            }

            var width = Math.Max(1, (int)Math.Ceiling(window.Bounds.Width));
            var height = Math.Max(1, (int)Math.Ceiling(window.Bounds.Height));
            if (width < 100 || height < 100)
            {
                width = 1280;
                height = 820;
            }

            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
            bitmap.Render(window);
            bitmap.Save(dest);
            Console.WriteLine("LOCALTS_SMOKE_SCREENSHOT=" + dest);
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Smoke screenshot failed: " + ex);
            Console.Error.Flush();
        }
    }
}

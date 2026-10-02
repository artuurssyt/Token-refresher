using Avalonia;
using Avalonia.X11;

namespace LocaltsAccountManager.Desktop;

internal static class Program
{
    public static string[] Args { get; private set; } = Array.Empty<string>();

    public static bool SmokeMode =>
        Args.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase))
        || string.Equals(Environment.GetEnvironmentVariable("LOCALTS_SMOKE"), "1", StringComparison.Ordinal);

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Localts Account Manager failed to start:");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        if (!OperatingSystem.IsWindows())
        {
            builder = builder.With(new X11PlatformOptions
            {
                RenderingMode = new[]
                {
                    X11RenderingMode.Software,
                    X11RenderingMode.Egl,
                    X11RenderingMode.Glx
                }
            });
        }

        return builder;
    }
}

using DonutHypixelPlayerComparer.Infrastructure;
using DonutHypixelPlayerComparer.UI;

namespace DonutHypixelPlayerComparer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Handlers go on before anything that can fail, so a first-run problem is reported rather
        // than surfacing as a bare CLR crash dialog.
        Application.ThreadException += (_, e) => Report(e.Exception, "Unexpected error");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Background threads — the bridge listener and scan tasks — would otherwise take the
            // process down without a word.
            if (e.ExceptionObject is Exception exception) Report(exception, "Unexpected background error");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            BridgeDebugLog.Write("Unobserved task exception: " + e.Exception.GetType().Name, e.Exception.Message);
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        try
        {
            AppPaths.EnsureCreated();
            // Nothing prunes the response cache while the app runs, so sweep stale entries once.
            AppPaths.PruneCache(TimeSpan.FromDays(7));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"The application data folder could not be created:{Environment.NewLine}{AppPaths.DataDirectory}"
                + $"{Environment.NewLine}{Environment.NewLine}{ex.Message}"
                + $"{Environment.NewLine}{Environment.NewLine}Settings and cached responses will not be saved.",
                "Storage unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Application.Run(new MainForm());
    }

    private static void Report(Exception exception, string caption)
    {
        BridgeDebugLog.Write(caption + ": " + exception.GetType().Name, exception.ToString());
        try
        {
            MessageBox.Show(exception.Message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception) { /* The UI may already be gone; the log entry above is the record. */ }
    }
}

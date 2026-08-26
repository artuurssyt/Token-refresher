using DonutComparer.Core.Infrastructure;
using DonutHypixelPlayerComparer.UI;

namespace DonutHypixelPlayerComparer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        AppPaths.EnsureCreated();
        Application.ThreadException += (_, e) => MessageBox.Show(
            e.Exception.Message,
            "Unexpected error",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        Application.Run(new MainForm());
    }
}

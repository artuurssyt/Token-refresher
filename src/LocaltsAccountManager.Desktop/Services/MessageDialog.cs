using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LocaltsAccountManager.Desktop.Services;

public enum MessageDialogKind
{
    Info,
    Warning,
    Error
}

public static class MessageDialog
{
    public static async Task ShowAsync(Window? owner, string title, string message, MessageDialogKind kind)
    {
        await ShowCoreAsync(owner, title, message, confirm: false, kind).ConfigureAwait(true);
    }

    public static Task<bool> ConfirmAsync(Window? owner, string title, string message, bool warning) =>
        ShowCoreAsync(owner, title, message, confirm: true, warning ? MessageDialogKind.Warning : MessageDialogKind.Info);

    private static async Task<bool> ShowCoreAsync(
        Window? owner,
        string title,
        string message,
        bool confirm,
        MessageDialogKind kind)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 16)
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        if (confirm)
        {
            var no = new Button { Content = "No", MinWidth = 80 };
            no.Click += (_, _) => dialog.Close();
            var yes = new Button { Content = "Yes", MinWidth = 80 };
            yes.Click += (_, _) =>
            {
                result = true;
                dialog.Close();
            };
            buttons.Children.Add(no);
            buttons.Children.Add(yes);
        }
        else
        {
            var ok = new Button { Content = "OK", MinWidth = 80 };
            ok.Click += (_, _) => dialog.Close();
            buttons.Children.Add(ok);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children = { text, buttons }
        };

        if (owner != null)
        {
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            var tcs = new TaskCompletionSource<bool>();
            dialog.Closed += (_, _) => tcs.TrySetResult(true);
            dialog.Show();
            await tcs.Task.ConfigureAwait(true);
        }

        return result;
    }
}

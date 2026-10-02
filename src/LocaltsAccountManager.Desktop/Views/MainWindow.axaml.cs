using Avalonia.Controls;
using Avalonia.Interactivity;
using LocaltsAccountManager.ViewModels;

namespace LocaltsAccountManager.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        try
        {
            await vm.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await Services.MessageDialog.ShowAsync(
                this,
                "Startup",
                $"Could not load saved accounts.\n\n{ex.GetType().Name}: {ex.Message}",
                Services.MessageDialogKind.Warning).ConfigureAwait(true);
        }
    }

    private void AccountMenuButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        if (button.DataContext is AccountLibraryItemViewModel item && DataContext is MainViewModel vm)
        {
            vm.SelectedLibraryAccount = item;
        }

        button.ContextMenu.Open(button);
        e.Handled = true;
    }

    private void CopyUsername_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CopyLibraryUsernameCommand.Execute(vm.SelectedLibraryAccount);
        }
    }

    private void CopyUuid_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CopyLibraryUuidCommand.Execute(vm.SelectedLibraryAccount);
        }
    }

    private void CopyAccessToken_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CopyLibraryAccessTokenCommand.Execute(vm.SelectedLibraryAccount);
        }
    }

    private void CopyRefreshToken_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.CopyLibraryRefreshTokenCommand.Execute(vm.SelectedLibraryAccount);
        }
    }
}

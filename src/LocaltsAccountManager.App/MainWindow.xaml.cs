using System.Windows;
using LocaltsAccountManager.App.ViewModels;

namespace LocaltsAccountManager.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        // async void event handler: an exception escaping here reaches the dispatcher handler as a
        // bare message box, so surface the load failure with context instead.
        try
        {
            await vm.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not load saved accounts.\n\n{ex.GetType().Name}: {ex.Message}",
                "Startup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void AccountMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu == null)
        {
            return;
        }

        // ContextMenu is not in the visual tree — bind commands to the window VM,
        // and keep the row item on the button Tag / selected library account.
        if (button.DataContext is AccountLibraryItemViewModel item && DataContext is MainViewModel vm)
        {
            vm.SelectedLibraryAccount = item;
        }

        button.ContextMenu.DataContext = DataContext;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    private void LocaltsApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is System.Windows.Controls.PasswordBox box)
        {
            vm.LocaltsApiKeyInput = box.Password;
        }
    }
}

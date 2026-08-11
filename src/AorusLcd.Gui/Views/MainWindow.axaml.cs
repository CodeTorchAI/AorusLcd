using System.Threading.Tasks;
using Avalonia;
using AorusLcd.Gui.ViewModels;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AorusLcd.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ImagePicker = () => PickFileAsync("Choose an image for the LCD",
                ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"]);
            vm.GifPicker = () => PickFileAsync("Choose a GIF for the LCD", ["*.gif"]);
            vm.InstallerLaunchConfirmation = ConfirmInstallerLaunchAsync;
        }
    }

    private async Task<bool> ConfirmInstallerLaunchAsync(string title, string message)
    {
        var launchButton = new Button { Content = "Launch installer", MinWidth = 120 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 90 };
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, launchButton },
                    },
                },
            },
        };

        launchButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<string?> PickFileAsync(string title, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Files") { Patterns = patterns }],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
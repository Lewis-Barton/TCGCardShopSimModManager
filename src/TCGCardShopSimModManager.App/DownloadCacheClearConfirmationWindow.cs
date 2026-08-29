using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TCGCardShopSimModManager.App;

public sealed class DownloadCacheClearConfirmationWindow : Window
{
    public DownloadCacheClearConfirmationWindow(string cacheSize)
    {
        Title = "Clear downloaded mod files";
        Width = 470;
        Height = 245;
        MinWidth = 420;
        MinHeight = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var clear = new Button
        {
            Content = "Clear downloads",
            Classes = { "danger" }
        };
        clear.Click += (_, _) => Close(true);
        var cancel = new Button
        {
            Content = "Cancel",
            IsDefault = true,
            IsCancel = true,
            Classes = { "secondary" }
        };
        cancel.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"Clear {cacheSize} of downloaded mod files?",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Installed mods will not be changed. Cleared archives will be downloaded again when a future install or update needs them.",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, clear }
                }
            }
        };
    }
}

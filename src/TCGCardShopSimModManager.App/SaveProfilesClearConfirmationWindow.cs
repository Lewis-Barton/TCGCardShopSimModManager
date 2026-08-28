using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TCGCardShopSimModManager.App;

public sealed class SaveProfilesClearConfirmationWindow : Window
{
    public SaveProfilesClearConfirmationWindow(string storageSize, int profileCount)
    {
        Title = "Clear stored modpack saves";
        Width = 500;
        Height = 270;
        MinWidth = 440;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var clear = new Button { Content = "Delete stored saves" };
        clear.Click += (_, _) => Close(true);
        var cancel = new Button { Content = "Cancel", Classes = { "secondary" } };
        cancel.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"Delete {storageSize} of saves stored for {profileCount:N0} modpack{(profileCount == 1 ? string.Empty : "s")}?",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "The saves currently used by the game will not be changed. Stored progress for other modpacks cannot be recovered after it is deleted.",
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

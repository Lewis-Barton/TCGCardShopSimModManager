using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.App;

public sealed class ModUninstallConfirmationWindow : Window
{
    public ModUninstallConfirmationWindow(string modName, ModInventoryState state)
    {
        Title = "Confirm mod uninstall";
        Width = 480;
        Height = 270;
        MinWidth = 420;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var uninstall = new Button { Content = "Uninstall mod", Classes = { "danger" } };
        uninstall.Click += (_, _) => Close(true);
        var cancel = new Button { Content = "Cancel", Classes = { "secondary" } };
        cancel.Click += (_, _) => Close(false);

        var detail = state == ModInventoryState.Modified
            ? "This mod has changed files. Only files that still match the install journal will be removed; changed files will be kept and reported."
            : "Only files that still match the install journal will be removed. Files changed after installation will be kept and reported.";

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"Uninstall {modName}?",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, uninstall }
                }
            }
        };
    }
}

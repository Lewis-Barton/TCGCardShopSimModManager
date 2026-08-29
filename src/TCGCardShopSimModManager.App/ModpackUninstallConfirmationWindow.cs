using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TCGCardShopSimModManager.App;

public sealed class ModpackUninstallConfirmationWindow : Window
{
    public ModpackUninstallConfirmationWindow(string packName)
    {
        Title = "Confirm modpack uninstall";
        Width = 470;
        Height = 270;
        MinWidth = 420;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var uninstall = new Button
        {
            Content = "Uninstall modpack",
            Classes = { "danger" }
        };
        uninstall.Click += (_, _) => Close(true);
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
                    Text = $"Uninstall {packName}?",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = "Every mod installed by this pack will be removed. Files that existed before the manager installed the pack will be kept. If a managed file was edited, the uninstall will stop and restore any pack files already removed.",
                    TextWrapping = TextWrapping.Wrap
                },
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

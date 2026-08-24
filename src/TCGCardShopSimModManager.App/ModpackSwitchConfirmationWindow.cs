using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.App;

public sealed class ModpackSwitchConfirmationWindow : Window
{
    public ModpackSwitchConfirmationWindow(
        string currentPackName,
        string nextPackName,
        ModpackSwitchPlan plan)
    {
        Title = "Confirm modpack switch";
        Width = 500;
        Height = 330;
        MinWidth = 440;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var switchButton = new Button { Content = "Switch modpacks" };
        switchButton.Click += (_, _) => Close(true);
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
                    Text = $"Switch from {currentPackName} to {nextPackName}?",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Mods shared by both packs will be kept or updated. Mods used only by the current pack will be removed before the remaining files are installed. If the switch cannot finish, the manager will restore the current pack.",
                    TextWrapping = TextWrapping.Wrap
                },
                new Border
                {
                    Classes = { "card" },
                    Child = new TextBlock
                    {
                        Text = $"Keep {plan.Retained.Count} · Update {plan.Updated.Count} · Remove {plan.Removed.Count} · Add {plan.Added.Count}",
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, switchButton }
                }
            }
        };
    }
}

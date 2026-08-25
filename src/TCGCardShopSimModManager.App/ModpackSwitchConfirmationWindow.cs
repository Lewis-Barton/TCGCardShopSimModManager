using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        Width = 620;
        Height = 560;
        MinWidth = 500;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var switchButton = new Button { Content = "Switch modpacks" };
        switchButton.Click += (_, _) => Close(true);
        var cancel = new Button { Content = "Cancel", Classes = { "secondary" } };
        cancel.Click += (_, _) => Close(false);

        var changes = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                ChangeGroup("Keep", "Already matches the new pack", plan.Retained),
                ChangeGroup("Update", "A different pinned archive will be installed", plan.Updated),
                ChangeGroup("Remove", "Used only by the current pack", plan.Removed),
                ChangeGroup("Add", "New in the selected pack", plan.Added)
            }
        };

        var heading = new TextBlock
        {
            Text = $"Switch from {currentPackName} to {nextPackName}?",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        var explanation = new TextBlock
        {
            Text = "Review the changes below before continuing. If the switch cannot finish, the manager will restore the current pack.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 12)
        };
        var summary = new TextBlock
        {
            Text = $"Keep {plan.Retained.Count} · Update {plan.Updated.Count} · Remove {plan.Removed.Count} · Add {plan.Added.Count}",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, switchButton }
        };
        var changeList = new ScrollViewer
        {
            Content = changes,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var content = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            Children =
            {
                heading,
                explanation,
                summary,
                changeList,
                actions
            }
        };
        Grid.SetRow(explanation, 1);
        Grid.SetRow(summary, 2);
        Grid.SetRow(changeList, 3);
        Grid.SetRow(actions, 4);
        actions.Margin = new Thickness(0, 14, 0, 0);
        Content = content;
    }

    private static Border ChangeGroup(string heading, string description, IReadOnlyList<string> mods)
    {
        var names = mods.Count == 0
            ? "None"
            : string.Join(Environment.NewLine, mods.Select(name => $"• {name}"));
        return new Border
        {
            Classes = { "card" },
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{heading} ({mods.Count})",
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = description,
                        Classes = { "subtitle" },
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock { Text = names, TextWrapping = TextWrapping.Wrap }
                }
            }
        };
    }
}

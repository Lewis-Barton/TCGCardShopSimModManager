using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.App;

public sealed class OptionalSelectionConfirmationWindow : Window
{
    public OptionalSelectionConfirmationWindow(string packName, IReadOnlyList<ModEntry> selectedOptionalMods)
    {
        Title = "Confirm optional mods";
        Width = 480;
        Height = 390;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var choices = new StackPanel { Spacing = 5 };
        if (selectedOptionalMods.Count == 0)
        {
            choices.Children.Add(new TextBlock
            {
                Text = "No optional mods selected.",
                FontWeight = FontWeight.SemiBold
            });
        }
        else
        {
            foreach (var mod in selectedOptionalMods)
            {
                var version = string.IsNullOrWhiteSpace(mod.Version) ? string.Empty : $" {mod.Version}";
                choices.Children.Add(new TextBlock { Text = $"• {mod.Name}{version}", TextWrapping = TextWrapping.Wrap });
            }
        }

        var install = new Button
        {
            Content = "Install with these options",
            IsDefault = true
        };
        install.Click += (_, _) => Close(true);
        var back = new Button
        {
            Content = "Go back",
            IsCancel = true,
            Classes = { "secondary" }
        };
        back.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "Review optional mods",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    Text = $"{packName} will always install its required mods. These optional choices will also be installed:",
                    TextWrapping = TextWrapping.Wrap
                },
                new ScrollViewer { MaxHeight = 220, Content = choices },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { back, install }
                }
            }
        };
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.App;

public sealed class SaveProfilesManageWindow : Window
{
    public SaveProfilesManageWindow(IReadOnlyList<StoredModpackSaveProfile> profiles)
    {
        Title = "Manage stored modpack saves";
        Width = 560;
        Height = 430;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var profileList = new ListBox
        {
            ItemsSource = profiles.Select(profile =>
                $"{profile.PackId} — {profile.FileCount:N0} file{(profile.FileCount == 1 ? string.Empty : "s")}, {FormatBytes(profile.SizeBytes)}")
                .ToList()
        };
        var delete = new Button
        {
            Content = "Delete selected save",
            IsEnabled = false
        };
        profileList.SelectionChanged += (_, _) =>
            delete.IsEnabled = profileList.SelectedIndex >= 0;
        delete.Click += (_, _) =>
        {
            if (profileList.SelectedIndex is var index && index >= 0 && index < profiles.Count)
                Close(profiles[index].PackId);
        };
        var cancel = new Button { Content = "Cancel", Classes = { "secondary" } };
        cancel.Click += (_, _) => Close(null);

        Content = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = "Choose stored progress to delete",
                    FontSize = 20,
                    FontWeight = FontWeight.SemiBold
                },
                new TextBlock
                {
                    [Grid.RowProperty] = 1,
                    Margin = new Thickness(0, 10, 0, 12),
                    Text = "The saves currently used by the game will not be changed. Deleted stored progress cannot be recovered.",
                    TextWrapping = TextWrapping.Wrap
                },
                new Border
                {
                    [Grid.RowProperty] = 2,
                    Child = profileList
                },
                new StackPanel
                {
                    [Grid.RowProperty] = 3,
                    Margin = new Thickness(0, 12, 0, 0),
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, delete }
                }
            }
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:N0} {units[unit]}" : $"{value:N1} {units[unit]}";
    }
}

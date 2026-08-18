using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenWSR.App;

/// <summary>A small themed prompt for a placefile URL, since WPF has no input box.</summary>
public static class PlacefilePrompt
{
    public static string? Ask(Window owner)
    {
        var box = new TextBox { Margin = new Thickness(0, 8, 0, 0), MinWidth = 430 };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Placefile URL",
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Paste a GRLevelX placefile address. OpenWSR appends the map centre as "
                 + "lat/lon and a version, the way GR does, so generators that expect them work.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.75,
            Margin = new Thickness(0, 4, 0, 0),
            MaxWidth = 430,
        });
        panel.Children.Add(box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", Width = 84, IsCancel = true };
        var ok = new Button { Content = "Add", Width = 84, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Add placefile",
            Content = panel,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)Application.Current.Resources["Chrome"],
            Foreground = (Brush)Application.Current.Resources["Text"],
        };
        DarkTitleBar.Apply(dialog);
        ok.Click += (_, _) => dialog.DialogResult = true;
        box.Focus();

        return dialog.ShowDialog() == true ? box.Text : null;
    }
}

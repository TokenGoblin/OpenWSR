using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OpenWSR.App;

/// <summary>
/// The reference cards: keyboard shortcuts, the storm symbol key, and About. All three
/// used to live somewhere a user would never look — a paragraph at the bottom of a
/// MessageBox, or a box of shapes wedged into the layers panel.
/// </summary>
public static class InfoWindow
{
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Keyboard shortcuts and mouse gestures. Opened by the rail's ? button and F1.</summary>
    public static void ShowShortcuts(Window owner)
    {
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 520 };
        AddHeading(panel, "Over the map", first: true);
        AddRows(panel,
            ("R  V  W  D  P  C", "Reflectivity · Velocity · Spectrum width · ZDR · PhiDP · CC"),
            ("↑  ↓", "Step up and down through the elevation cuts"),
            ("Drag", "Pan, with inertia"),
            ("Wheel", "Zoom about the cursor"),
            ("Hover", "Read value, azimuth, ranges and beam height"),
            ("Right-drag", "Whatever tool is armed in the rail — measure, or slice"),
            ("Click", "Storm cell or warning polygon details"));

        AddHeading(panel, "Anywhere");
        AddRows(panel,
            ("F1", "This card"),
            ("Enter in the search box", "Find a city, ZIP code or lat,lon"));

        AddNote(panel,
            "The product buttons above the map carry the same letters, so the shortcuts are "
          + "the buttons — there is nothing extra to remember.");

        Show(owner, "Keyboard and mouse", panel);
    }

    /// <summary>What the storm overlay's shapes mean. Was a cramped grid in the layers panel.</summary>
    public static void ShowSymbolKey(Window owner)
    {
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 460 };
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        (FrameworkElement Glyph, string Name, string Meaning)[] rows =
        [
            (new Path
            {
                Data = Geometry.Parse("M 7,0 L 14,7 L 7,14 L 0,7 Z"),
                Fill = Brushes.WhiteSmoke,
            }, "Cell now", "Where the SCIT algorithm places the storm on this scan"),
            (new Line
            {
                X1 = 0, Y1 = 7, X2 = 20, Y2 = 7,
                Stroke = Brushes.WhiteSmoke, StrokeThickness = 1.5,
            }, "Past track", "Where it has been, one mark per previous scan"),
            (new Line
            {
                X1 = 0, Y1 = 7, X2 = 20, Y2 = 7,
                Stroke = Brushes.WhiteSmoke, StrokeThickness = 1.5,
                StrokeDashArray = [2, 2],
            }, "Forecast + cone", "Projected path in 15-minute steps, and the area it could sweep"),
            (new Path
            {
                Data = Geometry.Parse("M 7,0 L 14,13 L 0,13 Z"),
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x78, 0x28)),
                StrokeThickness = 1.5,
            }, "Hail", "Sized by probability of severe hail; orange past 30%"),
            (new Ellipse
            {
                Width = 14, Height = 14,
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xDC, 0x28)),
                StrokeThickness = 1.5,
            }, "Mesocyclone", "Detected rotation — the ring is its actual radius"),
        ];

        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var (glyph, name, meaning) = rows[i];
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.HorizontalAlignment = HorizontalAlignment.Left;
            glyph.Margin = new Thickness(0, 9, 0, 9);
            Grid.SetRow(glyph, i);
            grid.Children.Add(glyph);

            var text = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            text.Children.Add(new TextBlock
            {
                Text = name,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("Text"),
            });
            text.Children.Add(new TextBlock
            {
                Text = meaning,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("TextDim"),
            });
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
        }

        panel.Children.Add(grid);
        AddNote(panel, "Click any cell on the map for its full attributes.");
        Show(owner, "Storm symbols", panel);
    }

    public static void ShowAbout(Window owner, string version)
    {
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 500 };
        panel.Children.Add(new TextBlock
        {
            Text = $"OpenWSR {version}",
            FontSize = 19,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("Text"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "An open-source native NEXRAD Level II radar viewer.",
            Foreground = Res("TextDim"),
            Margin = new Thickness(0, 3, 0, 0),
        });

        var warning = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x22, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x8E, 0x40, 0x48)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 16, 0, 0),
        };
        var warningText = new StackPanel();
        warningText.Children.Add(new TextBlock
        {
            Text = "NOT FOR LIFE-SAFETY DECISIONS",
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0xC8)),
        });
        warningText.Children.Add(new TextBlock
        {
            Text = "For informational and educational use only. Never rely on it for warnings "
                 + "or protective action — use official National Weather Service products and "
                 + "your local warning systems.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0xC0)),
        });
        warning.Child = warningText;
        panel.Children.Add(warning);

        AddHeading(panel, "Data");
        AddRows(panel,
            ("Level II / III", "NOAA NEXRAD via AWS Open Data (NSF Unidata)"),
            ("Warnings", "api.weather.gov"),
            ("Mosaic, SPC, reports", "Iowa Environmental Mesonet"),
            ("Forecast", "NOAA HRRR via AWS Open Data"),
            ("Basemap", "© OpenStreetMap contributors"));

        AddNote(panel, "Component licences are listed in THIRD-PARTY-NOTICES.md.");
        Show(owner, "About OpenWSR", panel);
    }

    // ---- building blocks ----

    private static void AddHeading(Panel panel, string text, bool first = false)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Foreground = Res("TextDim"),
            Margin = new Thickness(0, first ? 0 : 20, 0, 6),
        });
    }

    private static void AddRows(Panel panel, params (string Left, string Right)[] rows)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var left = new TextBlock
            {
                Text = rows[i].Left,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                Foreground = Res("Text"),
                Margin = new Thickness(0, 3, 18, 3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(left, i);
            grid.Children.Add(left);

            var right = new TextBlock
            {
                Text = rows[i].Right,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Res("TextDim"),
                Margin = new Thickness(0, 3, 0, 3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(right, i);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        panel.Children.Add(grid);
    }

    private static void AddNote(Panel panel, string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextDim"),
            Margin = new Thickness(0, 18, 0, 0),
        });
    }

    private static void Show(Window owner, string title, FrameworkElement content)
    {
        var root = new DockPanel();
        var close = new Button
        {
            Content = "Close",
            Width = 88,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 18),
        };
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        root.Children.Add(new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 620,
        });

        var window = new Window
        {
            Title = title,
            Content = root,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = Res("Chrome"),
            Foreground = Res("Text"),
        };
        DarkTitleBar.Apply(window);
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
    }
}

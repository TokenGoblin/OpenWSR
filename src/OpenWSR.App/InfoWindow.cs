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

    /// <summary>
    /// Keyboard shortcuts and mouse gestures. Opened by the rail's ? button and F1.
    ///
    /// It carries the way through to About as well. About had its own rail slot, which is a
    /// permanent 44 px of a strip that had run out of room, for a card read once. Both are
    /// reference material and this is where reference material lives.
    /// </summary>
    public static void ShowShortcuts(Window owner, string version)
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

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var guide = new Button { Content = "How this works", Padding = new Thickness(12, 5, 12, 5) };
        var about = new Button
        {
            Content = $"About OpenWSR {version}",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 5, 12, 5),
        };
        row.Children.Add(guide);
        row.Children.Add(about);
        panel.Children.Add(row);

        guide.Click += (_, _) => { Window.GetWindow(guide)?.Close(); ShowGuide(owner); };
        about.Click += (_, _) => { Window.GetWindow(about)?.Close(); ShowAbout(owner, version); };

        Show(owner, "Keyboard and mouse", panel);
    }

    /// <summary>
    /// How the app works, for someone who has just opened it.
    ///
    /// Reference material, so it lives here rather than in a panel — but unlike the shortcut
    /// and symbol cards this one is prose, because the questions it answers are not "what does
    /// this button do" but "what will this thing do on my behalf, and when". The alerting
    /// section is the one that earns the window: it decides what interrupts someone, and a
    /// person who cannot predict that either stops trusting it or stops reading it.
    /// </summary>
    public static void ShowGuide(Window owner)
    {
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 620 };

        AddHeading(panel, "Start here", first: true);
        AddParagraph(panel,
            "On its first run the app asks Windows where this PC is and saves that as Home, "
          + "which is all the setup there is — it then opens on your nearest radar and watches "
          + "for storms heading your way. If Windows would not say (the two location switches "
          + "in Privacy & security both have to be on), or you want somewhere else watched, "
          + "open Settings — the gear at the bottom of the left rail — and use MY PLACES. "
          + "“Add my location” asks again; “Move primary on map” hands you back to the map to "
          + "click a spot. Everything below follows from having at least one place.");
        AddRows(panel,
            ("Primary", "The place the app opens on, and whose nearest radar the storm layer follows. Exactly one, marked by the filled dot."),
            ("Other places", "Watched for approaching storms just the same — they simply do not move the camera."),
            ("Per-place radius", "How far out to care, for that place. “Default” uses the global setting below the list."));

        AddHeading(panel, "How the storm alarm works");
        AddParagraph(panel,
            "Two circles decide everything, and they answer different questions.");
        AddRows(panel,
            ("Alert radius", "What gets watched. A storm whose track never comes inside this is ignored entirely."),
            ("Interrupt within", "What gets to take your attention. Inside this, a storm raises a tray notification and a sound."));
        AddParagraph(panel,
            "Between the two circles a storm is real but not coming for you, and the app says so "
          + "rather than treating it the same. Every tracked cell resolves to one of three things:");
        AddRows(panel,
            ("Direct", "Its forecast track passes within the interrupt radius and it is still closing. Tray notification, sound, and a bright row in the APPROACHING panel."),
            ("Glancing", "It comes inside the alert radius but misses. A muted row saying which side it passes — “22 mi to your N” — and no interruption."),
            ("Receding", "Its closest approach is already behind it. Not shown at all: the panel is titled APPROACHING and a departing storm contradicts the title."));
        AddParagraph(panel,
            "Two things always interrupt regardless. A warning polygon near or over a place — a "
          + "polygon carries no track, so there is nothing to judge it as coming or going — and a "
          + "cell with a detected mesocyclone, because its forecast track is the part of it least "
          + "worth betting on.");
        AddParagraph(panel,
            "Each threat notifies once an hour at most, per place. The same storm crossing two of "
          + "your places is two separate notifications, at two distances and two arrival times, "
          + "because hearing about it at home should not use up the alert for the office.");

        AddHeading(panel, "Leaving it watching");
        AddParagraph(panel,
            "None of the above happens while OpenWSR is not running, so closing the window puts "
          + "it in the notification area rather than shutting it down. It keeps checking there — "
          + "storm tracks every two minutes, warnings every minute — and notifies you exactly as "
          + "it would with the window open. The live radar stream stops meanwhile — it is by "
          + "far the expensive part and the alerting does not use it — and so does every other "
          + "layer that refreshes itself, satellite and the mosaics included. A hidden OpenWSR "
          + "does nothing but watch. Opening the window starts them all again.");
        AddRows(panel,
            ("The tray icon", "Hover it for what is being watched, or what is coming. Double-click to open the window."),
            ("Right-click it", "Open, Settings, or Exit — and Exit is how you actually quit once the window is hidden."),
            ("Settings", "RUNNING IN THE BACKGROUND has switches for what closing and minimising do, for starting straight into the tray, and for starting with Windows."));

        AddHeading(panel, "Watching from another screen");
        AddParagraph(panel,
            "OpenWSR can serve a page to the other devices on your network — a phone, a wall "
          + "tablet, a Home Assistant dashboard — showing the radar, the warnings, the storm "
          + "cells and the same APPROACHING list as this window. It is off until you switch it "
          + "on under DASHBOARD ON YOUR NETWORK in Settings, which also lists the addresses to "
          + "open. It keeps working while OpenWSR is in the tray, because it only shows what "
          + "the two watching polls have already found. Anyone on your network can open it, "
          + "with no password, and it shows your saved places.");
        AddRows(panel,
            ("Home Assistant", "Add a Webpage card with the address from Settings. Home Assistant opened over https cannot show an http page inside it — open it over http on your network."),
            ("One part only", "Add ?view=map, ?view=threats or ?view=storms to the address, and &theme=light for a light page."),
            ("A red bar on the page", "OpenWSR has stopped answering, or the warnings have not refreshed for five minutes. What is shown may be out of date."),
            ("The radar on the page", "Iowa State's national mosaic, refreshed about every five minutes — not this window's own radar."));

        AddHeading(panel, "What the panel is telling you");
        AddRows(panel,
            ("APPROACHING", "Something is on course. The heading turns orange."),
            ("IN THE AREA", "Storms are inside a radius but none are on course for you."),
            ("“6 · 1 passing wide”", "Six coming, one merely going by. The two claims are counted separately rather than added up."),
            ("A row", "Click it to put the camera on that storm. Hover for the full detail line."),
            ("Nothing at all", "Nothing is threatening any of your places. The panel hides itself rather than showing an empty heading."));
        AddParagraph(panel,
            "Storm tracks come from the radar's own algorithms, not from the picture on screen — "
          + "the nearest WSR-88D publishes cell positions and 15-minute forecast points. Terminal "
          + "radars do not publish them, so the storm layer always follows the nearest WSR-88D "
          + "even while you are looking at a TDWR.");

        AddHeading(panel, "Reading the radar");
        AddRows(panel,
            ("Product bar", "REF is how much is falling, VEL is motion toward and away. A greyed-out button means this volume does not carry that product."),
            ("Tilt", "Which elevation cut. The up and down arrows over the map step through them. A WSR-88D volume has twenty-odd; a TDWR has three."),
            ("WSR-88D", "The 163-strong national network. The only source carrying dual-pol, so debris and hail size live here."),
            ("TDWR", "47 terminal radars beside major airports — the four-letter IDs starting with T. Finer beam, lower to the ground, faster, but reflectivity and velocity only, live only, and they go quiet in clear air."));

        AddHeading(panel, "Layers, and what fights what");
        AddParagraph(panel,
            "The national mosaic, the MRMS composite and the forecast raster are all the same "
          + "quantity as the radar — a reflectivity field — so only one may sit under the sweep at "
          + "a time. Turning one on turns the others off; that is by design, not a bug.");
        AddParagraph(panel,
            "Satellite is different. Cloud is a separate measurement from precipitation and the "
          + "whole point is seeing it underneath, so it coexists with everything. Clear ground is "
          + "left transparent so the map still reads through it.");

        AddHeading(panel, "The forecast page");
        AddParagraph(panel,
            "The cloud button at the foot of the rail is the one screen here that is not radar. "
          + "It answers the question the map cannot — what the weather will do — with the "
          + "National Weather Service forecast for your place, in the forecaster's own words, a "
          + "week ahead.");
        AddRows(panel,
            ("Current conditions", "A real reading from a real instrument, not model output. The card names the station and how far away it is, because that distance is the difference between “it is 75° outside” and “it is 75° at the airport”."),
            ("Observed at", "Which station. The nearest one that is actually reporting is picked for you; the list is every station the NWS carries nearby, nearest first, and stations go quiet often enough that the nearest is frequently not the closest."),
            ("Your place, not the map", "It forecasts for your saved place wherever the camera has wandered to. With no place saved it uses the middle of the map and says so."),
            ("Personal stations", "Optional. The nearest official station can be twenty miles off; a neighbour's is usually one or two, but there is no free way to read them — Weather Underground issues keys only to people running a station and uploading to it. These keys also expire; when one lapses the page says so and carries on with the official stations."),
            ("Your own station", "If you own an Ambient Weather station, its two keys read it directly — the closest reading there is, and the only source that can say whether it is raining on your own roof. It is preferred over everything else while it is reporting. Both keys are made at ambientweather.net/account."));
        AddParagraph(panel,
            "It refreshes itself every ten minutes while open, and closes when the app goes to "
          + "the notification area — a forecast is something you look at, not something worth "
          + "keeping a clock running for while nobody is there.");

        AddParagraph(panel,
            "Surface stations is the layer that reads the ground rather than the sky: a dot per weather station with its temperature. Official stations need no key and appear as soon as it is ticked; personal ones and your own join them once their keys are set in Settings, coloured apart. Behind a squall line the ten-degree temperature drop is often the more useful number, and no radar product shows it.");

        AddHeading(panel, "When");
        AddRows(panel,
            ("LIVE", "Streamed as the antenna turns — a tilt appears about five seconds after the radar sweeps it."),
            ("ARCHIVE", "Any site, any UTC day back to 1991. Scrub the day or play a loop."),
            ("FORECAST", "HRRR model reflectivity for the next six hours. A forecast, not a measurement."));

        AddNote(panel,
            "The tools in the rail arm one gesture at a time, so a right-drag always means "
          + "exactly one thing — measure, or slice a cross-section. The status bar says which.");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var shortcuts = new Button { Content = "Keyboard and mouse", Padding = new Thickness(12, 5, 12, 5) };
        var symbols = new Button
        {
            Content = "Storm symbols",
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 5, 12, 5),
        };
        buttons.Children.Add(shortcuts);
        buttons.Children.Add(symbols);
        panel.Children.Add(buttons);

        string version = owner.GetType().Assembly.GetName().Version?.ToString(3) ?? "dev";
        shortcuts.Click += (_, _) =>
        {
            Window.GetWindow(shortcuts)?.Close();
            ShowShortcuts(owner, version);
        };
        symbols.Click += (_, _) =>
        {
            Window.GetWindow(symbols)?.Close();
            ShowSymbolKey(owner);
        };

        Show(owner, "How OpenWSR works", panel);
    }

    /// <summary>A wrapped paragraph. The other cards are tables; this one has to explain.</summary>
    private static void AddParagraph(Panel panel, string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextDim"),
            Margin = new Thickness(0, 0, 0, 10),
        });
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

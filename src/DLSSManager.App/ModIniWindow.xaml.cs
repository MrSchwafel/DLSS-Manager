using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DLSSManager.Core;

namespace DLSSManager.App;

public partial class ModIniWindow : Window
{
    private readonly List<IniTab> tabs = [];

    public Dictionary<IniMod, IniProfile> Changes { get; } = [];

    public ModIniWindow(string? gameTitle, IniMod initialTab, IReadOnlyDictionary<IniMod, byte[]> originals,
        IReadOnlyDictionary<IniMod, IniProfile> global, IReadOnlyDictionary<IniMod, IniProfile> local, int? recommendedFrames,
        IReadOnlyDictionary<IniMod, string>? iniPaths = null)
    {
        InitializeComponent();
        TargetText.Text = gameTitle ?? "Globale Standard-Vorlagen";
        if (gameTitle is null) SaveButton.Content = "Globale Standards speichern";
        foreach (var mod in Enum.GetValues<IniMod>())
        {
            var pane = new IniTab(mod, originals[mod], global[mod], local[mod], gameTitle is not null, recommendedFrames, this,
                iniPaths?.GetValueOrDefault(mod), OpenIniFile);
            pane.StatusChanged += UpdateProfileBadge;
            tabs.Add(pane);
            Tabs.Items.Add(new TabItem
            {
                Header = new TextBlock { Text = $"{(mod == IniMod.Sm86 ? "SM86 FrameGen" : "OptiScaler")} ({ModIniProfiles.FileName(mod)})" },
                Content = pane.View
            });
        }
        Tabs.SelectedIndex = (int)initialTab;
        UpdateProfileBadge();
    }

    private void TabSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.Source == Tabs) UpdateProfileBadge();
    }

    private void UpdateProfileBadge()
    {
        var index = Tabs.SelectedIndex;
        if (index < 0 || index >= tabs.Count) return;
        ProfileBadge.Tag = tabs[index].ProfileKind;
        ProfileText.Text = tabs[index].ProfileStatus;
    }

    private void OpenIniFile(string path)
    {
        try
        {
            SafePaths.RejectLinks(path);
            if (!File.Exists(path)) throw new IOException($"Die Spiel-INI fehlt: {path}");
            var editor = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"));
            editor.ArgumentList.Add(path);
            using var process = System.Diagnostics.Process.Start(editor);
            ErrorText.Text = "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ErrorText.Text = e.Message;
        }
    }

    private void Save(object sender, RoutedEventArgs args)
    {
        Changes.Clear();
        foreach (var tab in tabs.Where(t => t.Dirty))
        {
            try { Changes.Add(tab.Mod, tab.Collect()); }
            catch (ArgumentException ex)
            {
                Tabs.SelectedIndex = (int)tab.Mod;
                ErrorText.Text = ex.Message;
                return;
            }
        }
        DialogResult = true;
    }

    private sealed class IniTab
    {
        private readonly byte[] original;
        private readonly IniProfile inherited;
        private readonly IniField[] schema;
        private readonly Style buttonStyle;
        private readonly int? recommendedFrames;
        private readonly bool forGame;
        private readonly bool hasSavedGameProfile;
        private IniProfile draft;
        private bool updatingFrames;
        private readonly TextBox search = new()
        {
            Width = 260, Height = 34, Padding = new(31, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center
        };
        private readonly ComboBox filter = new()
        {
            ItemsSource = new[] { "Wichtigste Einstellungen", "Alle INI-Sektionen" },
            SelectedIndex = 0, Width = 215, Height = 34, Margin = new(8, 0, 0, 0),
            ToolTip = "Angezeigte Einstellungen"
        };
        private readonly TextBox expert = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 85, MaxHeight = 180, Padding = new(6) };
        private readonly StackPanel sections = new();
        private Button? deleteProfile;
        private readonly List<(IniField Field, FrameworkElement Row, Control Input, CheckBox Override)> fields = [];

        public IniMod Mod { get; }
        public bool Dirty { get; private set; }
        public FrameworkElement View { get; }
        public string ProfileKind { get; private set; } = "";
        public string ProfileStatus { get; private set; } = "";
        public event Action? StatusChanged;

        public IniTab(IniMod mod, byte[] original, IniProfile global, IniProfile local, bool forGame, int? recommendedFrames, ModIniWindow window,
            string? iniPath, Action<string> openIniFile)
        {
            Mod = mod;
            buttonStyle = (Style)window.FindResource("IniButton");
            this.original = original;
            this.forGame = forGame;
            hasSavedGameProfile = forGame && local.HasOverrides;
            this.recommendedFrames = recommendedFrames;
            inherited = forGame ? global : IniProfile.Empty;
            draft = forGame ? local : global;
            schema = ModIniSchema.Read(mod, original);
            var toolbar = new StackPanel();
            var divider = new Border
            {
                Child = toolbar, BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                BorderThickness = new(0, 0, 0, 1), Padding = new(0, 0, 10, 10), Margin = new(0, 0, 0, 5)
            };
            DockPanel.SetDock(divider, Dock.Top);
            var filters = new DockPanel { LastChildFill = false };
            toolbar.Children.Add(filters);
            var searchAndFilter = new StackPanel { Orientation = Orientation.Horizontal };
            filters.Children.Add(searchAndFilter);
            if (forGame && iniPath is not null)
            {
                var open = CreateAction(window, "IniFileButton", "Spiel-INI öffnen", () => openIniFile(iniPath), "\uE7C3");
                open.IsEnabled = File.Exists(iniPath);
                open.ToolTip = iniPath + (open.IsEnabled
                    ? "\nÖffnet die Datei in Notepad. Dauerhafte Änderungen bitte im Profil speichern."
                    : "\nDie Spiel-INI ist noch nicht vorhanden.");
                ToolTipService.SetShowOnDisabled(open, true);
                DockPanel.SetDock(open, Dock.Right);
                filters.Children.Add(open);
            }
            AutomationProperties.SetName(search, "Einstellung suchen");
            var searchBox = new Grid();
            searchBox.Children.Add(search);
            var icon = ToolbarIcon("\uE721");
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            icon.Margin = new(11, 0, 0, 0);
            icon.Foreground = (Brush)window.FindResource("Muted");
            searchBox.Children.Add(icon);
            var placeholder = new TextBlock
            {
                Text = "Einstellung suchen…", IsHitTestVisible = false, Foreground = icon.Foreground,
                Margin = new(32, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            };
            searchBox.Children.Add(placeholder);
            AutomationProperties.SetName(filter, "Angezeigte Einstellungen");
            searchAndFilter.Children.Add(searchBox);
            searchAndFilter.Children.Add(filter);
            var actionRow = new Grid { Margin = new(0, 8, 0, 0) };
            actionRow.ColumnDefinitions.Add(new());
            actionRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            toolbar.Children.Add(actionRow);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actionRow.Children.Add(actions);
            if (mod == IniMod.Sm86)
            {
                AddAction("Empfehlung: Max. MFG (6X)", () =>
                {
                    ReplaceFields(new("FrameGeneration", "Engine", "DLSS"), new("DLSSG", "Optimized", "2"),
                        new("FrameGeneration", "MaxGeneratedFrames", "5"), new("DLSSG", "Preset", "A"));
                }, "IniPresetButton", "\uE945");
                if (forGame && recommendedFrames is int frames)
                    AddAction($"DLSSdb ({frames + 1}X)", () =>
                        ReplaceFields(new IniSetting("FrameGeneration", "MaxGeneratedFrames", frames.ToString(CultureInfo.InvariantCulture))))
                        .ToolTip = "Spielspezifische Frame-Generation-Empfehlung aus DLSSdb übernehmen";
            }
            if (forGame)
                AddAction("Globale Standards übernehmen", () => Load(inherited, dirty: true));
            AddAction("Auf Standard zurücksetzen", ResetToDefaults).ToolTip = mod == IniMod.Sm86
                ? "SM86-Einstellungen auf Mod-Standard zurücksetzen"
                : "OptiScaler-Einstellungen mit automatischem Standard auf auto zurücksetzen";
            if (hasSavedGameProfile)
            {
                deleteProfile = CreateAction(window, "IniDeleteButton", "Profil löschen",
                    () => Load(IniProfile.Empty, dirty: true), "\uE74D");
                deleteProfile.ToolTip = "Gespeichertes Spielprofil löschen und auf Global-Standard zurückfallen";
                Grid.SetColumn(deleteProfile, 1);
                actionRow.Children.Add(deleteProfile);
            }
            var content = new StackPanel { Margin = new(0, 0, 14, 0) };
            content.Children.Add(sections);
            var help = new TextBlock { Text = "Expertenwerte · Abschnitt|Schlüssel=Wert", Margin = new(0, 12, 0, 4) };
            content.Children.Add(help);
            AutomationProperties.SetLabeledBy(expert, help);
            content.Children.Add(expert);
            var dock = new DockPanel();
            dock.Children.Add(divider);
            dock.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            View = dock;
            Load(draft, dirty: false);
            search.TextChanged += (_, _) =>
            {
                placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Filter();
            };
            filter.SelectionChanged += (_, _) => Filter();
            expert.TextChanged += (_, _) => { Dirty = true; RefreshStatus(); };

            Button AddAction(string title, Action action, string style = "IniActionButton", string? glyph = null)
            {
                var button = CreateAction(window, style, title, action, glyph);
                button.Margin = new(0, 0, 8, 0);
                actions.Children.Add(button);
                return button;
            }
        }

        private static TextBlock ToolbarIcon(string glyph) => new()
        {
            Text = glyph, FontFamily = new("Segoe MDL2 Assets"), FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };

        private static Button CreateAction(ModIniWindow window, string style, string title, Action action, string? glyph = null)
        {
            var button = new Button { Style = (Style)window.FindResource(style), Content = title };
            if (glyph is not null)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = ToolbarIcon(glyph);
                icon.Margin = new(0, 0, 6, 0);
                content.Children.Add(icon);
                content.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
                button.Content = content;
            }
            AutomationProperties.SetName(button, title);
            button.Click += (_, _) => action();
            return button;
        }

        private void ResetToDefaults()
        {
            var defaults = schema.Select(f => f.Default);
            if (Mod == IniMod.OptiScaler)
            {
                Load(new(defaults.Where(v => v.Value.Equals("auto", StringComparison.OrdinalIgnoreCase)).ToArray(), []), dirty: true);
                return;
            }

            var inheritedSettings = inherited.Values.Concat(inherited.Expert).ToArray();
            Load(new(defaults.Where(v => inheritedSettings.Any(setting => setting.Matches(v.Section, v.Key)) ||
                (forGame && recommendedFrames is not null && v.Matches("FrameGeneration", "MaxGeneratedFrames"))).ToArray(), []), dirty: true);
        }

        private void ReplaceFields(params IniSetting[] values)
        {
            var current = CollectValues(validateNumbers: false);
            var remaining = current.Where(v => !values.Any(p => v.Matches(p.Section, p.Key)));
            var expertText = expert.Text;
            Load(new([.. remaining, .. values], []), dirty: true);
            expert.Text = expertText;
        }

        private void Load(IniProfile profile, bool dirty)
        {
            draft = profile;
            expert.Text = string.Join(Environment.NewLine, profile.Expert.Select(v => v.ExpertLine));
            fields.Clear();
            sections.Children.Clear();
            var inheritedValues = ModIniService.ReadValues(ModIniProfiles.Merge(Mod, original, inherited,
                IniProfile.Empty, recommendedFrames));
            foreach (var group in schema.GroupBy(f => f.Default.Section, StringComparer.OrdinalIgnoreCase))
            {
                var heading = new TextBlock { Text = $"[{group.Key}]", FontWeight = FontWeights.SemiBold,
                    Margin = new(0, 9, 0, 5) };
                var panel = new StackPanel { Margin = new(0, 0, 0, 8) };
                panel.Children.Add(heading);
                sections.Children.Add(panel);
                var accent = Application.Current.FindResource("Accent");
                if (Mod == IniMod.Sm86 && group.Key is "DLSSG" or "XeSSFG")
                    heading.Tag = accent;
                foreach (var field in group)
                {
                    var key = field.Default;
                    var own = profile.Values.LastOrDefault(v => v.Matches(key.Section, key.Key));
                    var fallback = inheritedValues.LastOrDefault(v => v.Matches(key.Section, key.Key))?.Value ?? key.Value;
                    var (editor, input) = CreateInput(field, own?.Value ?? fallback);
                    var toggle = new CheckBox { Content = "Überschreiben", IsChecked = own is not null, Margin = new(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetName(toggle, $"{key.Section} {key.Key} überschreiben");
                    var label = new TextBlock { Text = key.Key, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetLabeledBy(input, label);
                    var row = new Grid { Margin = new(0, 0, 0, 4), ToolTip = Tooltip(field, fallback) };
                    ToolTipService.SetShowDuration(row, 25000);
                    row.ColumnDefinitions.Add(new() { Width = new(2, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new() { Width = new(3, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                    Grid.SetColumn(editor, 1);
                    Grid.SetColumn(toggle, 2);
                    row.Children.Add(label);
                    row.Children.Add(editor);
                    row.Children.Add(toggle);
                    editor.IsEnabled = own is not null;
                    toggle.Checked += (_, _) => { editor.IsEnabled = true; MarkEdited(); };
                    toggle.Unchecked += (_, _) =>
                    {
                        editor.IsEnabled = false;
                        SetValue(input, fallback);
                        MarkEdited();
                    };
                    if (input is ComboBox combo)
                    {
                        combo.SelectionChanged += (_, _) => { if (!updatingFrames) MarkEdited(); };
                        if (combo.IsEditable) combo.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => MarkEdited()));
                    }
                    else if (input is TextBox box) box.TextChanged += (_, _) => MarkEdited();
                    panel.Children.Add(row);
                    ToolTipService.SetShowOnDisabled(row, true);
                    fields.Add((field, row, input, toggle));
                }
            }
            Filter();
            HighlightEngine();
            RefreshStatus();
            Dirty = dirty;
        }

        private static TextBlock Tooltip(IniField field, string inherited) => new()
        {
            Text = $"Mod-Standard: {field.Default.Value}\nGeerbt: {inherited}" +
                (field.Description.Length == 0 ? "" : "\n\n" + field.Description),
            MaxWidth = 530, TextWrapping = TextWrapping.Wrap
        };

        private (FrameworkElement Editor, Control Input) CreateInput(IniField field, string value)
        {
            if (field.Choices.Count > 0)
            {
                var combo = new ComboBox { DisplayMemberPath = nameof(IniChoice.Label), SelectedValuePath = nameof(IniChoice.Value),
                    IsEditable = field.EditableChoices };
                combo.ItemsSource = WithCurrent(field.Choices, value);
                SetValue(combo, value);
                return (combo, combo);
            }
            var box = new TextBox { Text = value, Padding = new(4), VerticalContentAlignment = VerticalAlignment.Center };
            if (field.Number is not { } number) return (box, box);
            var group = new DockPanel();
            var spinner = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(spinner, Dock.Right);
            group.Children.Add(spinner);
            AddStep("−", -number.Step);
            AddStep("+", number.Step);
            group.Children.Add(box);
            return (group, box);

            void AddStep(string title, double amount)
            {
                var button = new Button { Style = buttonStyle, Content = title, Padding = new(7, 0, 7, 0), MinHeight = 24, Margin = new(3, 0, 0, 0) };
                AutomationProperties.SetName(button, amount < 0 ? "Wert verringern" : "Wert erhöhen");
                button.Click += (_, _) =>
                {
                    if (!TryNumber(box.Text, out var current)) current = 0;
                    box.Text = Math.Clamp(current + amount, number.Min, number.Max).ToString("0.##########", CultureInfo.InvariantCulture);
                };
                spinner.Children.Add(button);
            }
        }

        private void MarkEdited()
        {
            Dirty = true;
            RefreshStatus();
            HighlightEngine();
        }

        private void RefreshStatus()
        {
            var custom = fields.Any(f => f.Override.IsChecked == true) || !string.IsNullOrWhiteSpace(expert.Text) ||
                draft.Values.Any(v => !schema.Any(f => v.Matches(f.Default.Section, f.Default.Key)));
            (ProfileKind, ProfileStatus) = (forGame, custom, inherited.HasOverrides) switch
            {
                (true, true, _) => ("Game", "Eigenes Spielprofil aktiv"),
                (_, true, _) or (_, _, true) => ("Global", "Globaler Standard aktiv"),
                _ when Mod == IniMod.Sm86 && forGame && recommendedFrames is { } frames => ("Mod", $"DLSSdb-Empfehlung aktiv · {frames + 1}X"),
                _ => ("Mod", "Mod-Standard")
            };
            if (deleteProfile is not null)
                deleteProfile.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            StatusChanged?.Invoke();
        }

        private void HighlightEngine()
        {
            if (Mod != IniMod.Sm86) return;
            var engine = fields.FirstOrDefault(f => f.Field.Default.Matches("FrameGeneration", "Engine"));
            var xess = engine.Input is not null && ValueOf(engine.Input).Equals("XeSS", StringComparison.OrdinalIgnoreCase);
            foreach (var panel in sections.Children.OfType<StackPanel>())
            {
                var heading = (TextBlock)panel.Children[0];
                if (heading.Tag is Brush accent)
                    heading.Foreground = (heading.Text == "[XeSSFG]") == xess ? accent :
                        (Brush)Application.Current.FindResource("Muted");
            }
            var frames = fields.FirstOrDefault(f => f.Field.Default.Matches("FrameGeneration", "MaxGeneratedFrames"));
            if (frames.Input is ComboBox combo)
            {
                var options = frames.Field.Choices.Where(c => !xess || int.Parse(c.Value, CultureInfo.InvariantCulture) <= 3).ToArray();
                var value = ValueOf(combo);
                if (xess && int.TryParse(value, out var count) && count > 3) value = "3";
                var available = WithCurrent(options, value);
                if (combo.ItemsSource is IEnumerable<IniChoice> old && old.SequenceEqual(available)) return;
                updatingFrames = true;
                try
                {
                    combo.ItemsSource = available;
                    SetValue(combo, value);
                }
                finally { updatingFrames = false; }
            }
        }

        private void Filter()
        {
            var query = search.Text.Trim();
            foreach (var f in fields)
            {
                var key = f.Field.Default;
                var matches = query.Length == 0 || $"{key.Section} {key.Key} {f.Field.Description}".Contains(query, StringComparison.OrdinalIgnoreCase);
                f.Row.Visibility = matches && (filter.SelectedIndex == 1 || f.Field.Important) ? Visibility.Visible : Visibility.Collapsed;
            }
            foreach (var section in sections.Children.OfType<StackPanel>())
                section.Visibility = section.Children.OfType<Grid>().Any(r => r.Visibility == Visibility.Visible) ?
                    Visibility.Visible : Visibility.Collapsed;
        }

        public IniProfile Collect()
        {
            var result = new IniProfile(CollectValues(validateNumbers: true), ModIniService.ParseExpert(expert.Text));
            _ = ModIniProfiles.Merge(Mod, original, inherited, result, recommendedFrames);
            return result;
        }

        private IniSetting[] CollectValues(bool validateNumbers)
        {
            var result = draft.Values.Where(v => !fields.Any(f => v.Matches(f.Field.Default.Section, f.Field.Default.Key))).ToList();
            foreach (var field in fields.Where(f => f.Override.IsChecked == true))
            {
                var value = ValueOf(field.Input).Trim();
                if (validateNumbers && field.Field.Number is { } number &&
                    !(number.AllowsAuto && value.Equals("auto", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!TryNumber(value, out var numeric) || numeric < number.Min || numeric > number.Max ||
                        (number.Step == 1 && !field.Field.Default.Matches("Framerate", "FramerateLimit") && numeric != Math.Truncate(numeric)))
                        throw new ArgumentException($"Ungültige Zahl für {field.Field.Default.Section}|{field.Field.Default.Key}.");
                    value = numeric.ToString(CultureInfo.InvariantCulture);
                }
                result.Add(field.Field.Default with { Value = value });
            }
            return result.ToArray();
        }

        private static IEnumerable<IniChoice> WithCurrent(IReadOnlyList<IniChoice> choices, string value) =>
            choices.Any(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) ? choices : [.. choices, new(value, value)];

        private static void SetValue(Control input, string value)
        {
            if (input is TextBox box) box.Text = value;
            if (input is ComboBox combo)
            {
                var choice = combo.Items.OfType<IniChoice>().FirstOrDefault(c => c.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (choice is null && !combo.IsEditable)
                {
                    choice = new IniChoice(value, value);
                    combo.ItemsSource = combo.Items.OfType<IniChoice>().Append(choice).ToArray();
                }
                combo.SelectedItem = choice;
                if (combo.IsEditable) combo.Text = choice?.Label ?? value;
            }
        }

        private static string ValueOf(Control input) => input switch
        {
            TextBox box => box.Text,
            ComboBox { IsEditable: true } combo => combo.Text,
            ComboBox { SelectedItem: IniChoice item } => item.Value,
            _ => ""
        };

        private static bool TryNumber(string value, out double number) =>
            (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
             double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out number) ||
             double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) && double.IsFinite(number);
    }
}

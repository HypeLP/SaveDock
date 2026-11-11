using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using static SaveDock.L;

namespace SaveDock;

internal sealed class BackupSettingsDialog : GameDialog
{
    private readonly BackupOptions original;
    private readonly ComboBox language;
    private readonly ComboBox compression;
    private readonly ComboBox retention;
    private readonly ComboBox check;
    private readonly NumericUpDown count;
    private readonly StackPanel countGroup;
    private readonly TextBlock retentionHint;
    private readonly TextBlock checkHint;

    public BackupSettingsDialog(BackupOptions options, AppLanguage selectedLanguage) : base(T("Settings", "Einstellungen"), 535)
    {
        original = options;
        var layout = new Grid { Margin = new Thickness(22), RowDefinitions = new("*,Auto"), RowSpacing = 14 };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(Text(T("Settings", "Einstellungen"), heading: true));
        var languageLabel = T("Language", "Sprache");
        language = Picker(languageLabel, (int)selectedLanguage, "English", "Deutsch");
        content.Children.Add(Field(languageLabel, language));

        var compressionLabel = T("ZIP compression", "ZIP-Kompression");
        compression = Picker(compressionLabel, (int)options.Compression, T("Fast", "Schnell"),
            T("Compact", "Kompakt"), T("No compression", "Ohne Kompression"));
        content.Children.Add(Field(compressionLabel, compression));

        var checkLabel = T("Change detection", "Änderungsprüfung");
        check = Picker(checkLabel, (int)options.Check, T("Check file contents", "Dateiinhalt prüfen"),
            T("Quick check by file metadata", "Schnellprüfung nach Dateidaten"));
        content.Children.Add(Field(checkLabel, check));
        checkHint = Text(T("The quick check compares file size and timestamps. It can miss edits when those values stay the same.",
            "Die Schnellprüfung vergleicht Größe und Zeitstempel. Bei gleichen Werten können geänderte Inhalte unbemerkt bleiben."));
        content.Children.Add(checkHint);

        var retentionLabel = T("Retention per game", "Aufbewahrung pro Spiel");
        retention = Picker(retentionLabel, (int)options.Retention, T("Standard history", "Standardverlauf"),
            T("Keep latest version only", "Nur neueste Version"), T("Keep last X versions", "Letzte X Versionen"));
        content.Children.Add(Field(retentionLabel, retention));
        count = new NumericUpDown
        {
            Value = options.KeepVersions, Minimum = 2, Maximum = BackupOptions.MaxVersions, FormatString = "0",
            Increment = 1, HorizontalAlignment = HorizontalAlignment.Left, Width = 140
        };
        count.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, T("Number of versions per game", "Anzahl der Versionen pro Spiel"));
        countGroup = Field(T("Number of versions", "Anzahl der Versionen"), count);
        content.Children.Add(countGroup);
        retentionHint = Text("");
        content.Children.Add(retentionHint);
        Add(layout, new ScrollViewer { Content = content }, 0);

        var cancel = Action(T("Cancel", "Abbrechen"));
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(null);
        var save = Action(T("Save", "Speichern"), primary: true);
        save.Click += (_, _) => Close(new SettingsSelection((original with
        {
            Compression = (ZipCompression)compression.SelectedIndex,
            Retention = (RetentionMode)retention.SelectedIndex,
            KeepVersions = (int)(count.Value ?? original.KeepVersions),
            Check = (FileCheckMode)check.SelectedIndex
        }).Normalize(), language.SelectedIndex == (int)AppLanguage.German ? AppLanguage.German : AppLanguage.English));
        var buttons = Row(cancel, save);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        Add(layout, buttons, 1);
        Content = layout;

        retention.SelectionChanged += (_, _) => UpdateHints();
        check.SelectionChanged += (_, _) => UpdateHints();
        UpdateHints();
    }

    private void UpdateHints()
    {
        var selected = (RetentionMode)retention.SelectedIndex;
        countGroup.IsVisible = selected == RetentionMode.Count;
        checkHint.IsVisible = check.SelectedIndex == (int)FileCheckMode.Metadata;
        retentionHint.Text = selected switch
        {
            RetentionMode.Latest => T("Keep one full backup per game. The next successful backup removes older SaveDock versions. Backups with mapping.yaml are kept.",
                "Eine Vollsicherung pro Spiel. Ältere Versionen im SaveDock-Format werden beim nächsten erfolgreichen Backup entfernt. Sicherungen mit mapping.yaml bleiben erhalten."),
            RetentionMode.Count => T("A required full backup may also stay on disk. This setting takes effect on the next successful backup. Backups with mapping.yaml are kept.",
                "Eine nötige Vollsicherung kann zusätzlich auf der Festplatte bleiben. Die Auswahl gilt ab dem nächsten erfolgreichen Backup. Sicherungen mit mapping.yaml bleiben erhalten."),
            _ => T("Keep up to three full backups per game, each with up to five differential backups.",
                "Bis zu drei Vollsicherungen mit je fünf Differenzsicherungen pro Spiel.")
        };
    }

    private static ComboBox Picker(string name, int selected, params string[] items)
    {
        var picker = new ComboBox { ItemsSource = items, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        picker.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, name);
        return picker;
    }

    private static StackPanel Field(string label, Control control)
    {
        var field = new StackPanel { Spacing = 5 };
        field.Children.Add(Text(label));
        field.Children.Add(control);
        return field;
    }
}

internal sealed record SettingsSelection(BackupOptions Backup, AppLanguage Language);

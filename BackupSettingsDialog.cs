using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SaveDock;

internal sealed class BackupSettingsDialog : GameDialog
{
    private readonly BackupOptions original;
    private readonly ComboBox compression;
    private readonly ComboBox retention;
    private readonly ComboBox check;
    private readonly NumericUpDown count;
    private readonly StackPanel countGroup;
    private readonly TextBlock retentionHint;
    private readonly TextBlock checkHint;

    public BackupSettingsDialog(BackupOptions options) : base("Sicherungseinstellungen", 490)
    {
        original = options;
        var layout = new Grid { Margin = new Thickness(22), RowDefinitions = new("*,Auto"), RowSpacing = 14 };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(Text("Sicherungseinstellungen", heading: true));

        compression = Picker("ZIP-Kompression", (int)options.Compression, "Schnell", "Kompakt", "Ohne Kompression");
        content.Children.Add(Field("ZIP-Kompression", compression));

        check = Picker("Änderungsprüfung", (int)options.Check, "Dateiinhalt prüfen", "Schnellprüfung nach Dateidaten");
        content.Children.Add(Field("Änderungsprüfung", check));
        checkHint = Text("Die Schnellprüfung vergleicht Größe und Zeitstempel. Bei gleichen Werten können geänderte Inhalte unbemerkt bleiben.");
        content.Children.Add(checkHint);

        retention = Picker("Aufbewahrung pro Spiel", (int)options.Retention,
            "Standardverlauf", "Nur neueste Version", "Letzte X Versionen");
        content.Children.Add(Field("Aufbewahrung pro Spiel", retention));
        count = new NumericUpDown
        {
            Value = options.KeepVersions, Minimum = 2, Maximum = BackupOptions.MaxVersions, FormatString = "0",
            Increment = 1, HorizontalAlignment = HorizontalAlignment.Left, Width = 140
        };
        count.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, "Anzahl der Versionen pro Spiel");
        countGroup = Field("Anzahl der Versionen", count);
        content.Children.Add(countGroup);
        retentionHint = Text("");
        content.Children.Add(retentionHint);
        Add(layout, new ScrollViewer { Content = content }, 0);

        var cancel = Action("Abbrechen");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(null);
        var save = Action("Speichern", primary: true);
        save.Click += (_, _) => Close((original with
        {
            Compression = (ZipCompression)compression.SelectedIndex,
            Retention = (RetentionMode)retention.SelectedIndex,
            KeepVersions = (int)(count.Value ?? original.KeepVersions),
            Check = (FileCheckMode)check.SelectedIndex
        }).Normalize());
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
            RetentionMode.Latest => "Eine Vollsicherung pro Spiel. Ältere Versionen im SaveDock-Format werden beim nächsten erfolgreichen Backup entfernt. Sicherungen mit mapping.yaml bleiben erhalten.",
            RetentionMode.Count => "Eine nötige Vollsicherung kann zusätzlich auf der Festplatte bleiben. Die Auswahl gilt ab dem nächsten erfolgreichen Backup. Sicherungen mit mapping.yaml bleiben erhalten.",
            _ => "Bis zu drei Vollsicherungen mit je fünf Differenzsicherungen pro Spiel."
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

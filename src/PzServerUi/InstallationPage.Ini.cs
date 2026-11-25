using PzServerUi.Application;

namespace PzServerUi;

public partial class InstallationPage
{
    private sealed class IniDraft(ServerIniSettings settings)
    {
        public ServerIniSettings Settings { get; } = settings;
        public Dictionary<string, string> Changes { get; } = new(StringComparer.Ordinal);
        public string? Conflict { get; set; }
    }

    private readonly Dictionary<string, IniDraft> _iniDrafts = new(StringComparer.OrdinalIgnoreCase);
    private string? _iniProfilePath;
    private IniDraft? _iniDraft;
    private ServerIniGroup? _expandedIniGroup;
    private int _iniColumnCount = 1;
    private bool _loadingIni;

    private async void OnReloadIniClicked(object? sender, EventArgs e)
    {
        if (_profiles.Selected is not { } profile)
            return;
        if (_iniDraft?.Changes.Count > 0 &&
            !await DisplayAlertAsync("Перечитать INI?", "Несохранённые правки этого профиля будут потеряны.",
                "Перечитать", "Отмена"))
            return;
        if (_profiles.Selected?.ConfigurationPath == profile.ConfigurationPath)
            LoadIni(profile, reload: true);
    }

    private void LoadIni(ServerProfile? profile, bool reload = false)
    {
        if (!reload && _iniProfilePath is { } previous && _iniDraft is { Changes.Count: > 0 } draft)
            _iniDrafts[previous] = draft;
        _iniProfilePath = profile?.ConfigurationPath;
        _iniDraft = null;
        _expandedIniGroup = null;
        IniFields.Children.Clear();
        IniSearch.IsVisible = SaveIniButton.IsEnabled = IniMessage.IsVisible = false;
        if (profile is null)
            return;
        try
        {
            if (_iniDrafts.Remove(profile.ConfigurationPath, out var saved) && !reload)
                _iniDraft = saved;
            else
                _iniDraft = new(ServerIniSettings.Open(profile));
            _loadingIni = true;
            IniSearch.Text = "";
            IniSearch.IsVisible = true;
            SaveIniButton.IsEnabled = _iniDraft is { Conflict: null, Changes.Count: > 0 };
            if (_iniDraft.Conflict is { } conflict)
                ShowIniMessage(conflict);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowIniMessage($"Не удалось прочитать INI. {error.Message}");
        }
        finally { _loadingIni = false; }
        RenderIniFields();
    }

    private void OnIniSearchChanged(object? sender, TextChangedEventArgs e) => RenderIniFields(openSingleMatch: true);

    private void OnIniFieldsSizeChanged(object? sender, EventArgs e)
    {
        if (sender is not VisualElement { Width: > 0 } panel)
            return;
        var columns = panel.Width >= 730 ? 3 : panel.Width >= 480 ? 2 : 1;
        if (columns == _iniColumnCount)
            return;
        _iniColumnCount = columns;
        RenderIniFields();
    }

    private void RenderIniFields(bool openSingleMatch = false)
    {
        if (_loadingIni || _iniDraft is not { } draft)
            return;
        IniFields.Children.Clear();
        var search = IniSearch.Text;
        var groups = draft.Settings.Fields.Where(field =>
            (OverviewQueries.Matches(field.Key, search) || OverviewQueries.Matches(field.Label, search)
                || OverviewQueries.Matches(IniGroupName(field.Group), search)))
            .GroupBy(field => field.Group).ToArray();
        if (_expandedIniGroup is { } expanded && !groups.Any(group => group.Key == expanded))
            _expandedIniGroup = null;
        if (openSingleMatch && !string.IsNullOrWhiteSpace(search) && groups.Length == 1)
            _expandedIniGroup = groups[0].Key;
        if (groups.Length == 0)
        {
            IniFields.Children.Add(new Label { Text = "Параметры не найдены." });
            return;
        }

        var visibleColumns = Math.Min(_iniColumnCount, groups.Length);
        var headers = new Dictionary<ServerIniGroup, (Button Button, IGrouping<ServerIniGroup, ServerIniField> Group, ContentView Panel)>();
        ContentView? activePanel = null;
        foreach (var groupRow in groups.Chunk(visibleColumns))
        {
            var row = new Grid { ColumnSpacing = 6 };
            var panel = new ContentView { IsVisible = false };
            for (var column = 0; column < visibleColumns; column++)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
            for (var column = 0; column < groupRow.Length; column++)
            {
                var group = groupRow[column];
                var header = new Button { Style = (Style)Resources["IniGroupHeader"] };
                headers.Add(group.Key, (header, group, panel));
                Grid.SetColumn(header, column);
                row.Children.Add(header);
                header.Clicked += (_, _) =>
                {
                    _expandedIniGroup = _expandedIniGroup == group.Key ? null : group.Key;
                    UpdatePanels();
                };
            }
            IniFields.Children.Add(row);
            IniFields.Children.Add(panel);
        }
        UpdatePanels();

        void UpdatePanels()
        {
            if (activePanel is not null)
            {
                activePanel.IsVisible = false;
                activePanel.Content = null;
                activePanel = null;
            }
            foreach (var (key, (button, group, panel)) in headers)
            {
                var open = _expandedIniGroup == key;
                var name = IniGroupName(key);
                button.Text = $"{(open ? "▾" : "▸")}  {name} ({group.Count()})";
                button.BorderColor = Color.FromArgb(open ? "#786643" : "#34392E");
                button.BackgroundColor = Color.FromArgb(open ? "#302B21" : "#23271F");
                button.TextColor = Color.FromArgb(open ? "#D7B773" : "#EFEEE5");
                SemanticProperties.SetDescription(button, $"{name}, параметров: {group.Count()}, {(open ? "развёрнуто" : "свёрнуто")}");
                if (!open)
                    continue;

                var rows = new VerticalStackLayout { Spacing = 6 };
                foreach (var field in group)
                    rows.Children.Add(BuildIniField(draft, field));
                panel.Content = new Border { Style = (Style)Resources["Card"], Padding = 10, Content = rows };
                panel.IsVisible = true;
                activePanel = panel;
            }
        }
    }

    private View BuildIniField(IniDraft draft, ServerIniField field)
    {
        var label = field.Label == field.Key ? field.Key : $"{field.Label} ({field.Key})";
        var row = new VerticalStackLayout { Spacing = 2 };
        row.Children.Add(new Label { Text = label, LineBreakMode = LineBreakMode.WordWrap });
        if (field.IsAmbiguous)
        {
            row.Children.Add(new Label { Text = "Недоступно: дубликат или неверный регистр ключа в INI.", FontSize = 12 });
            return row;
        }
        var value = draft.Changes.GetValueOrDefault(field.Key, field.Value);
        if (field.Kind == ServerIniKind.Flag && bool.TryParse(value, out var enabled))
        {
            var toggle = new Switch { IsToggled = enabled, HorizontalOptions = LayoutOptions.Start };
            SemanticProperties.SetDescription(toggle, label);
            toggle.Toggled += (_, args) => Update(args.Value ? "true" : "false");
            row.Children.Add(toggle);
        }
        else
        {
            var entry = new Entry
            {
                Text = value, IsPassword = field.Kind == ServerIniKind.Secret,
                IsSpellCheckEnabled = false, IsTextPredictionEnabled = false
            };
            SemanticProperties.SetDescription(entry, label);
            entry.TextChanged += (_, args) => Update(args.NewTextValue ?? "");
            row.Children.Add(entry);
        }
        return row;

        void Update(string newValue)
        {
            if (_iniDraft != draft)
                return;
            if (newValue == field.Value || (field.Kind == ServerIniKind.Flag
                && bool.TryParse(newValue, out var desired) && bool.TryParse(field.Value, out var original) && desired == original))
                draft.Changes.Remove(field.Key);
            else
                draft.Changes[field.Key] = newValue;
            SaveIniButton.IsEnabled = draft.Conflict is null && draft.Changes.Count > 0;
        }
    }

    private void OnSaveIniClicked(object? sender, EventArgs e)
    {
        if (_iniDraft is not { Conflict: null } draft || _iniProfilePath != _profiles.Selected?.ConfigurationPath)
            return;
        try
        {
            var changed = draft.Settings.Save(draft.Changes);
            draft.Changes.Clear();
            SaveIniButton.IsEnabled = false;
            if (changed)
                MarkSettingsSaved();
            RenderIniFields();
            ShowIniMessage(changed ? "Настройки сохранены. Они вступят в силу при следующем запуске." : "Нет изменений для сохранения.");
        }
        catch (ProfileConfigurationConflictException error)
        {
            draft.Conflict = error.Message;
            SaveIniButton.IsEnabled = false;
            ShowIniMessage(error.Message);
        }
        catch (ArgumentException error)
        {
            ShowIniMessage(error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowIniMessage($"Не удалось сохранить INI. {error.Message}");
        }
    }

    private void ShowIniMessage(string message)
    {
        IniMessage.Text = message;
        IniMessage.IsVisible = true;
    }

    private static string IniGroupName(ServerIniGroup group) => group switch
    {
        ServerIniGroup.General => "Основные",
        ServerIniGroup.Access => "Доступ и безопасность",
        ServerIniGroup.Network => "Сеть и RCON",
        ServerIniGroup.Gameplay => "Игровой процесс",
        ServerIniGroup.Safehouses => "Убежища и фракции",
        ServerIniGroup.Chat => "Чат, голос и Discord",
        ServerIniGroup.Mods => "Моды и карты",
        ServerIniGroup.Maintenance => "Обслуживание и резервные копии",
        _ => "Прочие"
    };
}

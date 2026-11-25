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
    private ServerIniGroup[] _iniGroups = [];
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
        IniFields.Children.Clear();
        IniFilters.IsVisible = SaveIniButton.IsEnabled = IniMessage.IsVisible = false;
        if (profile is null)
            return;
        try
        {
            if (_iniDrafts.Remove(profile.ConfigurationPath, out var saved) && !reload)
                _iniDraft = saved;
            else
                _iniDraft = new(ServerIniSettings.Open(profile));
            _loadingIni = true;
            _iniGroups = _iniDraft.Settings.Fields.Select(field => field.Group).Distinct().ToArray();
            IniGroupPicker.ItemsSource = new[] { "Все группы" }.Concat(_iniGroups.Select(IniGroupName)).ToArray();
            IniGroupPicker.SelectedIndex = 0;
            IniSearch.Text = "";
            IniFilters.IsVisible = true;
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

    private void OnIniFilterChanged(object? sender, EventArgs e) => RenderIniFields();

    private void RenderIniFields()
    {
        if (_loadingIni || _iniDraft is not { } draft)
            return;
        IniFields.Children.Clear();
        var index = IniGroupPicker.SelectedIndex - 1;
        var search = IniSearch.Text;
        var matches = draft.Settings.Fields.Where(field =>
            (index < 0 || index >= _iniGroups.Length || _iniGroups[index] == field.Group) &&
            (OverviewQueries.Matches(field.Key, search) || OverviewQueries.Matches(field.Label, search)
                || OverviewQueries.Matches(IniGroupName(field.Group), search)));
        foreach (var group in matches.GroupBy(field => field.Group))
        {
            var rows = new VerticalStackLayout { Spacing = 6 };
            var title = new Label { Text = IniGroupName(group.Key), FontAttributes = FontAttributes.Bold, FontSize = 16 };
            SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level3);
            rows.Children.Add(title);
            foreach (var field in group)
                rows.Children.Add(BuildIniField(draft, field));
            IniFields.Children.Add(new Border
            {
                Stroke = new SolidColorBrush(Color.FromArgb("#34392E")),
                Padding = 10,
                Content = rows
            });
        }
        if (IniFields.Children.Count == 0)
            IniFields.Children.Add(new Label { Text = "Параметры не найдены." });
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

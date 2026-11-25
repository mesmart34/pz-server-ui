using System.Diagnostics;
using System.Globalization;
using Microsoft.Maui.Controls;
using Microsoft.UI.Windowing;
using PzServerUi.Application;
using WinRT.Interop;
using FolderPicker = global::Windows.Storage.Pickers.FolderPicker;

namespace PzServerUi;

public partial class InstallationPage : ContentPage
{
    private readonly DedicatedServerInstallation _installation = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PzServerUi", "dedicated-installation.txt"));
    private readonly ServerProfiles _profiles = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
        "Zomboid"));
    private readonly ServerLaunches _launches = new();
    private readonly HashSet<string> _launching = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _launchMessages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _pendingSettings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _settingsAtLaunch = new(StringComparer.OrdinalIgnoreCase);
    private long _settingsRevision;
    private ProfileRow[] _profileRows = [];
    private ServerProfileMods? _modSnapshot;
    private string? _consoleTail;
    private bool _compact;
    private bool _showCompactProfiles;
    private string? _savedInstallationFolder;
    private string? _scanInstallation;
    private Task<ExternalServerScan>? _scanInFlight;
    private ExternalServerScan? _external;
    private bool _updatingProfiles;
    private string? _consoleProfile;
    private bool _sendingConsole;
    private bool _closeInProgress;
    private bool _canClose;
    private string? _sandboxProfilePath;
    private SandboxSettings? _sandboxSettings;
    private string? _basicProfilePath;
    private BasicServerSettings? _basicSettings;
    private Microsoft.UI.Xaml.Window? _nativeWindow;

    public InstallationPage()
    {
        InitializeComponent();
        ConsoleLevelPicker.ItemsSource = new[] { "Все уровни", "LOG", "WARN", "ERROR", "DEBUG", "Без уровня" };
        ConsoleLevelPicker.SelectedIndex = 0;
        ShowPane(OverviewPane);

        try
        {
            RefreshSavedFolder();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Не удалось прочитать сохранённую папку. Выбери её заново.");
        }

        RefreshProfiles();
        var statusTimer = Dispatcher.CreateTimer();
        statusTimer.Interval = TimeSpan.FromSeconds(1);
        statusTimer.Tick += (_, _) => RefreshStatuses();
        var externalTimer = Dispatcher.CreateTimer();
        externalTimer.Interval = TimeSpan.FromSeconds(8);
        externalTimer.Tick += async (_, _) => await RefreshExternalServersAsync();
        Loaded += async (_, _) =>
        {
            statusTimer.Start();
            externalTimer.Start();
            if (_nativeWindow is null && Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window native)
            {
                _nativeWindow = native;
                native.AppWindow.Closing += OnWindowClosing;
            }
            await RefreshExternalServersAsync();
        };
        Unloaded += (_, _) =>
        {
            statusTimer.Stop();
            externalTimer.Stop();
            if (_nativeWindow is not null)
                _nativeWindow.AppWindow.Closing -= OnWindowClosing;
            _nativeWindow = null;
        };
    }

    private async void OnRefreshProfilesClicked(object? sender, EventArgs e)
    {
        RefreshProfiles();
        await RefreshExternalServersAsync();
    }

    private async Task<ExternalServerScan?> RefreshExternalServersAsync()
    {
        var installation = _savedInstallationFolder;
        if (installation is null)
            return null;

        if (_scanInFlight is not { IsCompleted: false } || _scanInstallation != installation)
        {
            _scanInstallation = installation;
            _scanInFlight = WindowsExternalServers.ScanAsync(installation);
        }
        var result = await _scanInFlight;
        if (_savedInstallationFolder != installation)
            return null;
        _external = result;
        RefreshStatuses();
        return result;
    }

    private void RefreshProfiles()
    {
        try
        {
            _profiles.Refresh();
            _profileRows = _profiles.Items.Select(profile => new ProfileRow(profile)).ToArray();
            ApplyProfileFilter();
            ShowSelectedProfile(_profiles.Selected);
            LoadOverview(_profiles.Selected);
            RefreshStatuses();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _profileRows = [];
            ApplyProfileFilter();
            ProfileListMessage.Text = $"Не удалось прочитать папку {_profiles.ConfigurationDirectory}. Проверь доступ и обнови список.";
            ProfileListMessage.IsVisible = true;
            ShowSelectedProfile(null);
        }
    }

    private void OnProfileSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (ProfileList is not null)
            ApplyProfileFilter();
    }

    private void ApplyProfileFilter()
    {
        _updatingProfiles = true;
        try
        {
            var rows = _profileRows.Where(row => OverviewQueries.Matches(row.Name, ProfileSearch.Text)).ToArray();
            ProfileList.ItemsSource = rows;
            ProfileList.SelectedItem = rows.FirstOrDefault(row => row.Name == _profiles.Selected?.Name);
            ProfileCountLabel.Text = $"Профили · {_profileRows.Length}";
            ProfileListMessage.Text = _profileRows.Length == 0
                ? $"Профили не найдены в {_profiles.ConfigurationDirectory}."
                : rows.Length == 0 ? "Нет совпадений." : "";
            ProfileListMessage.IsVisible = ProfileListMessage.Text.Length != 0;
        }
        finally
        {
            _updatingProfiles = false;
        }
    }

    private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingProfiles || e.CurrentSelection.FirstOrDefault() is not ProfileRow row)
            return;

        _profiles.Select(row.Name);
        ShowSelectedProfile(_profiles.Selected);
        if (_compact)
        {
            _showCompactProfiles = false;
            UpdateWorkspaceLayout();
            ToggleProfilesButton.Focus();
        }
    }

    private void OnWorkspaceSizeChanged(object? sender, EventArgs e)
    {
        if (DetailsArea is null || WorkspaceLayout.Width <= 0)
            return;
        var compact = WorkspaceLayout.Width < 750;
        if (_compact == compact)
            return;
        _compact = compact;
        _showCompactProfiles = false;
        UpdateWorkspaceLayout();
    }

    private void OnToggleProfilesClicked(object? sender, EventArgs e)
    {
        _showCompactProfiles = !_showCompactProfiles;
        UpdateWorkspaceLayout();
        if (_showCompactProfiles)
            ProfileSearch.Focus();
    }

    private void UpdateWorkspaceLayout()
    {
        ToggleProfilesButton.IsVisible = _compact;
        ToggleProfilesButton.Text = _showCompactProfiles ? "К экземпляру" : "Профили";
        WorkspaceLayout.ColumnDefinitions[0].Width = _compact ? new GridLength(1, GridUnitType.Star) : new GridLength(248);
        WorkspaceLayout.ColumnDefinitions[1].Width = _compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        WorkspaceLayout.ColumnSpacing = _compact ? 0 : 12;
        Grid.SetColumn(DetailsArea, _compact ? 0 : 1);
        ProfilesCard.IsVisible = !_compact || _showCompactProfiles;
        DetailsArea.IsVisible = !_compact || !_showCompactProfiles;
    }

    private void ShowSelectedProfile(ServerProfile? profile)
    {
        ProfileDetails.IsVisible = profile is not null;
        ProfileDetailsPlaceholder.IsVisible = profile is null;
        ProfileDetailsPlaceholder.Text = "Нет выбранного профиля.";
        var changed = !string.Equals(_consoleProfile, profile?.Name, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            _consoleProfile = profile?.Name;
            ConsoleInput.Text = "";
            MaskConsoleInput.IsChecked = true;
            ConsoleOutput.Text = "";
            _consoleTail = null;
            ConsoleMessage.IsVisible = false;
        }
        if (!string.Equals(_sandboxProfilePath, profile?.ConfigurationPath, StringComparison.OrdinalIgnoreCase))
            LoadSandbox(profile);
        if (ProfilePane.IsVisible && _basicProfilePath != profile?.ConfigurationPath)
            LoadBasic(profile);
        if (ModsPane.IsVisible)
            RefreshProfileMods(profile);
        if (profile is null)
            return;

        ProfileNameLabel.Text = profile.Name;
        if (changed)
            LoadOverview(profile);
        SelectedCover.Source = _profileRows.FirstOrDefault(row => row.Name == profile.Name)?.Cover;
        ProfileConfigurationLabel.Text = profile.ConfigurationPath;
        ProfileWorldLabel.Text = profile.WorldPath ?? "Папка мира ещё не найдена.";
        RefreshSessionStatus(profile);
    }

    private void OnReloadSandboxClicked(object? sender, EventArgs e) => LoadSandbox(_profiles.Selected);

    private void LoadSandbox(ServerProfile? profile)
    {
        _sandboxProfilePath = profile?.ConfigurationPath;
        _sandboxSettings = null;
        SandboxFields.IsVisible = SaveSandboxButton.IsEnabled = false;
        SandboxMessage.IsVisible = false;
        if (profile is null)
            return;

        try
        {
            var settings = SandboxSettings.Open(profile);
            var choices = settings.DayLengths.Select(option => new DayLengthChoice(option.Code,
                option.Minutes switch
                {
                    1440 => "24 ч (реальное время)",
                    90 => "1 ч 30 мин",
                    < 60 => $"{option.Minutes} мин",
                    _ => $"{option.Minutes / 60} ч",
                })).ToList();
            if (!choices.Any(choice => choice.Code == settings.DayLength))
                choices.Insert(0, new(settings.DayLength, $"Оставить текущее значение (код {settings.DayLength})"));
            StarterKitBox.IsChecked = settings.StarterKit;
            TraitPointsEntry.Text = settings.CharacterFreePoints.ToString(CultureInfo.InvariantCulture);
            DayLengthPicker.ItemsSource = choices;
            DayLengthPicker.SelectedItem = choices.First(choice => choice.Code == settings.DayLength);
            _sandboxSettings = settings;
            SandboxFields.IsVisible = SaveSandboxButton.IsEnabled = true;
        }
        catch (FileNotFoundException)
        {
            ShowSandboxMessage("Файл песочницы для этого профиля не найден. Панель не создаёт его.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowSandboxMessage($"Не удалось открыть файл песочницы. {error.Message}");
        }
    }

    private void OnSaveSandboxClicked(object? sender, EventArgs e)
    {
        if (_sandboxSettings is not { } settings || DayLengthPicker.SelectedItem is not DayLengthChoice dayLength
            || !string.Equals(_sandboxProfilePath, _profiles.Selected?.ConfigurationPath, StringComparison.OrdinalIgnoreCase))
            return;
        if (!int.TryParse(TraitPointsEntry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var points)
            || (points != settings.CharacterFreePoints && points is < -100 or > 100))
        {
            ShowSandboxMessage("Введи целое число очков от −100 до 100.");
            return;
        }
        try
        {
            if (settings.Save(StarterKitBox.IsChecked, points, dayLength.Code))
            {
                MarkSettingsSaved();
                LoadSandbox(_profiles.Selected);
                ShowSandboxMessage("Настройки сохранены и вступят в силу при следующем запуске. "
                    + "Если сервер уже работает, останови и запусти его вручную.");
            }
            else
                ShowSandboxMessage("Нет изменений для сохранения.");
        }
        catch (ProfileConfigurationConflictException error)
        {
            SaveSandboxButton.IsEnabled = false;
            ShowSandboxMessage(error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowSandboxMessage($"Не удалось сохранить настройки. {error.Message}");
        }
    }

    private void ShowSandboxMessage(string message)
    {
        SandboxMessage.Text = message;
        SandboxMessage.IsVisible = true;
    }

    private sealed record DayLengthChoice(int Code, string Label)
    {
        public override string ToString() => Label;
    }

    private ServerSessionStatus GetSessionStatus(ServerProfile profile) => _launching.Contains(profile.Name)
        ? ServerSessionStatus.Starting : _launches.GetStatus(profile);

    private ExternalServerStatus GetExternalStatus(ServerProfile profile) =>
        _external?.GetStatus(profile.Name) ?? ExternalServerStatus.NotObserved;

    private void RefreshStatuses()
    {
        foreach (var row in _profileRows)
        {
            var status = GetSessionStatus(row.Profile);
            UpdatePendingSettings(row.Name, status);
            row.Update(OverviewQueries.Describe(status, GetExternalStatus(row.Profile), _external?.Available != false));
        }
        if (ProfileDetails.IsVisible && _profiles.Selected is { } profile)
            RefreshSessionStatus(profile);
    }

    private void RefreshSessionStatus(ServerProfile profile)
    {
        var status = GetSessionStatus(profile);
        var external = GetExternalStatus(profile);
        var presentation = OverviewQueries.Describe(status, external, _external?.Available != false);
        UpdatePendingSettings(profile.Name, status);
        _profileRows.FirstOrDefault(row => row.Name == profile.Name)?.Update(presentation);
        var ownActive = status is ServerSessionStatus.Starting or ServerSessionStatus.Running
            or ServerSessionStatus.Stopping or ServerSessionStatus.StopUnconfirmed;
        SessionStatusLabel.Text = presentation.Detail;
        SessionStatusLabel.SetAppThemeColor(Label.TextColorProperty,
            ProfileRow.GetColor(presentation.Tone, false), ProfileRow.GetColor(presentation.Tone, true));
        StartServerButton.IsEnabled = !_closeInProgress && _savedInstallationFolder is not null
            && !ownActive && external == ExternalServerStatus.NotObserved;
        StopServerButton.IsEnabled = !_closeInProgress && status == ServerSessionStatus.Running;
        StopHint.Text = _closeInProgress ? "Панель завершает работу." : presentation.StopHint;
        StopHint.IsVisible = !StopServerButton.IsEnabled && !string.IsNullOrEmpty(StopHint.Text);
        var pending = _pendingSettings.ContainsKey(profile.Name);
        PendingSettingsLabel.IsVisible = pending;
        PendingSettingsLabel.Text = "Изменения сохранены. Вступят в силу при следующем запуске.";
        OverviewNotice.IsVisible = pending;
        OverviewNotice.Text = PendingSettingsLabel.Text;
        LaunchMessageLabel.Text = _launchMessages.GetValueOrDefault(profile.Name)
            ?? (_savedInstallationFolder is null ? "Выбери папку Dedicated Server, чтобы запустить профиль." : "");
        LaunchMessageLabel.IsVisible = LaunchMessageLabel.Text.Length > 0;
        var canSend = !_sendingConsole && !_closeInProgress && !_launching.Contains(profile.Name)
            && status is ServerSessionStatus.Starting or ServerSessionStatus.Running;
        ConsoleInput.IsEnabled = SendConsoleButton.IsEnabled = canSend;
        if (ConsolePane.IsVisible)
            RefreshConsole(profile);
    }

    private void OnShowOverviewClicked(object? sender, EventArgs e) => ShowPane(OverviewPane);
    private void OnShowProfileClicked(object? sender, EventArgs e) => ShowPane(ProfilePane);
    private void OnShowModsClicked(object? sender, EventArgs e) => ShowPane(ModsPane);
    private void OnShowConsoleClicked(object? sender, EventArgs e) => ShowPane(ConsolePane);

    private void ShowPane(View pane)
    {
        OverviewPane.IsVisible = pane == OverviewPane;
        ProfilePane.IsVisible = pane == ProfilePane;
        ModsPane.IsVisible = pane == ModsPane;
        ConsolePane.IsVisible = pane == ConsolePane;
        DetailsScroll.IsVisible = !ConsolePane.IsVisible;
        SetTab(ShowOverviewButton, OverviewPane.IsVisible);
        SetTab(ShowProfileButton, ProfilePane.IsVisible);
        SetTab(ShowModsButton, ModsPane.IsVisible);
        SetTab(ShowConsoleButton, ConsolePane.IsVisible);
        if (OverviewPane.IsVisible)
            LoadOverview(_profiles.Selected);
        if (ModsPane.IsVisible)
            RefreshProfileMods(_profiles.Selected);
        if (ProfilePane.IsVisible && _basicProfilePath != _profiles.Selected?.ConfigurationPath)
            LoadBasic(_profiles.Selected);
        if (_profiles.Selected is { } profile)
            RefreshSessionStatus(profile);
    }

    private static void SetTab(Button button, bool selected)
    {
        button.SetAppThemeColor(Button.BackgroundColorProperty,
            Color.FromArgb(selected ? "#E5D7B8" : "#E6EADD"), Color.FromArgb(selected ? "#D7B773" : "#30382A"));
        button.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#262B21"),
            Color.FromArgb(selected ? "#231D10" : "#EFEEE5"));
        SemanticProperties.SetDescription(button, selected ? $"{button.Text}, текущая вкладка" : button.Text);
    }

    private void LoadOverview(ServerProfile? profile)
    {
        if (profile is null)
            return;
        BasicServerSettings? settings = null;
        try { settings = BasicServerSettings.Open(profile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
        OverviewLimit.Text = FormatSetting(settings?.MaxPlayers, 100);
        OverviewPvp.Text = settings is null ? "Не определено" : settings.Pvp ? "Включено" : "Выключено";
        OverviewPorts.Text = settings is null ? "Не определены"
            : $"{FormatSetting(settings.DefaultPort, 65535)} / {FormatSetting(settings.UdpPort, 65535)}";

        static string FormatSetting(string? value, int max) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= max
                ? number.ToString(CultureInfo.InvariantCulture) : "Не определено";
    }

    private void MarkSettingsSaved()
    {
        if (_profiles.Selected is not { } profile)
            return;
        _pendingSettings[profile.Name] = ++_settingsRevision;
        LoadOverview(profile);
        RefreshSessionStatus(profile);
    }

    private void UpdatePendingSettings(string name, ServerSessionStatus status)
    {
        if (status == ServerSessionStatus.Running && _settingsAtLaunch.Remove(name, out var revision)
            && _pendingSettings.GetValueOrDefault(name) == revision)
            _pendingSettings.Remove(name);
        else if (status is not (ServerSessionStatus.Starting or ServerSessionStatus.Running))
            _settingsAtLaunch.Remove(name);
    }

    private void OnReloadBasicClicked(object? sender, EventArgs e) => LoadBasic(_profiles.Selected);

    private void LoadBasic(ServerProfile? profile)
    {
        _basicProfilePath = profile?.ConfigurationPath;
        _basicSettings = null;
        BasicFields.IsVisible = SaveBasicButton.IsEnabled = BasicMessage.IsVisible = false;
        if (profile is null)
            return;
        try
        {
            var settings = BasicServerSettings.Open(profile);
            MaxPlayersEntry.Text = settings.MaxPlayers;
            PvpBox.IsChecked = settings.Pvp;
            DefaultPortEntry.Text = settings.DefaultPort;
            UdpPortEntry.Text = settings.UdpPort;
            _basicSettings = settings;
            BasicFields.IsVisible = SaveBasicButton.IsEnabled = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowBasicMessage($"Не удалось прочитать INI. {error.Message}");
        }
    }

    private void OnSaveBasicClicked(object? sender, EventArgs e)
    {
        if (_basicSettings is not { } settings || _basicProfilePath != _profiles.Selected?.ConfigurationPath)
            return;
        if (!TryReadNumber(MaxPlayersEntry, out var maxPlayers))
        {
            ShowBasicMessage("Введи целое число для лимита игроков (от 1 до 100).");
            return;
        }
        if (!TryReadNumber(DefaultPortEntry, out var defaultPort) || !TryReadNumber(UdpPortEntry, out var udpPort))
        {
            ShowBasicMessage("Введи целые числа для игровых портов (от 1 до 65535).");
            return;
        }
        try
        {
            if (settings.Save(maxPlayers, PvpBox.IsChecked, defaultPort, udpPort))
            {
                MarkSettingsSaved();
                ShowBasicMessage("Настройки сохранены. Они вступят в силу при следующем запуске.");
            }
            else
                ShowBasicMessage("Нет изменений для сохранения.");
        }
        catch (ProfileConfigurationConflictException error)
        {
            SaveBasicButton.IsEnabled = false;
            ShowBasicMessage(error.Message);
        }
        catch (ArgumentException error)
        {
            ShowBasicMessage(error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowBasicMessage($"Не удалось сохранить INI. {error.Message}");
        }

        static bool TryReadNumber(Entry entry, out int value) =>
            int.TryParse(entry.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private void ShowBasicMessage(string message)
    {
        BasicMessage.Text = message;
        BasicMessage.IsVisible = true;
    }

    private void RefreshProfileMods(ServerProfile? profile)
    {
        ModLists.IsVisible = ModsReadError.IsVisible = false;
        _modSnapshot = null;
        if (profile is null)
            return;
        try
        {
            _modSnapshot = ServerProfileMods.Read(profile);
            ApplyModFilter();
            ModLists.IsVisible = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ModsReadError.Text = error is InvalidDataException
                ? $"Не удалось прочитать список модов. {error.Message}"
                : "Не удалось прочитать INI этого профиля. Проверь файл и нажми «Обновить».";
            ModsReadError.IsVisible = true;
        }
    }

    private void OnModSearchChanged(object? sender, TextChangedEventArgs e) => ApplyModFilter();

    private void ApplyModFilter()
    {
        if (_modSnapshot is not { } mods)
            return;
        ModIdsLabel.Text = FormatModIds(mods.ModIds);
        WorkshopIdsLabel.Text = FormatModIds(mods.WorkshopIds);

        string FormatModIds(IReadOnlyList<string> ids)
        {
            if (ids.Count == 0)
                return "Не указаны.";
            var matches = ids.Where(id => OverviewQueries.Matches(id, ModSearch.Text)).ToArray();
            return matches.Length == 0 ? "Нет совпадений." : string.Join('\n', matches);
        }
    }

    private void OnConsoleSearchChanged(object? sender, TextChangedEventArgs e) => RenderConsole();
    private void OnConsoleLevelChanged(object? sender, EventArgs e) => RenderConsole();

    private void RefreshConsole(ServerProfile profile)
    {
        var tail = _launches.ReadConsole(profile);
        if (string.Equals(_consoleTail, tail, StringComparison.Ordinal))
            return;
        _consoleTail = tail;
        RenderConsole();
    }

    private void RenderConsole()
    {
        if (ConsoleOutput is null || ConsoleSearch is null || ConsoleLevelPicker is null)
            return;
        var tail = _consoleTail ?? "";
        var level = Math.Max(0, ConsoleLevelPicker.SelectedIndex);
        ConsoleOutput.Placeholder = tail.Length == 0
            ? "Журнал появится после запуска сервера из панели."
            : "Нет строк, соответствующих фильтрам.";
        var filtered = OverviewQueries.FilterLog(tail, ConsoleSearch.Text, (ConsoleLogLevel)level);
        if (!string.Equals(ConsoleOutput.Text, filtered, StringComparison.Ordinal))
            ConsoleOutput.Text = filtered;
    }

    private void OnMaskConsoleChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (ConsoleInput is not null)
            ConsoleInput.IsPassword = e.Value;
    }

    private async void OnSendConsoleClicked(object? sender, EventArgs e)
    {
        if (_closeInProgress || _sendingConsole || !ConsoleInput.IsEnabled || _profiles.Selected is not { } profile)
            return;

        var line = ConsoleInput.Text ?? "";
        ConsoleInput.Text = "";
        ConsoleMessage.IsVisible = false;
        _sendingConsole = true;
        RefreshSessionStatus(profile);
        try
        {
            var error = await _launches.SendConsoleLineAsync(profile, line);
            if (error is not null && _profiles.Selected?.Name == profile.Name)
            {
                ConsoleMessage.Text = error;
                ConsoleMessage.IsVisible = true;
            }
        }
        finally
        {
            _sendingConsole = false;
            if (_profiles.Selected is { } selected)
                RefreshSessionStatus(selected);
        }
    }

    private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_canClose)
            return;
        if (_closeInProgress)
        {
            args.Cancel = true;
            return;
        }

        var pending = _launches.PendingShutdownProfiles();
        if (_launching.Count == 0 && pending.Count == 0)
            return;

        args.Cancel = true;
        _closeInProgress = true;
        ShowSelectedProfile(_profiles.Selected);
        try
        {
            if (!await DisplayAlertAsync("Выход из панели",
                    "Серверы, запущенные из панели, будут штатно остановлены. Внешние серверы останутся работать.",
                    "Остановить серверы и выйти", "Отмена"))
                return;

            var timeout = Stopwatch.StartNew();
            do
            {
                _launches.RequestShutdownStops();
                pending = _launches.PendingShutdownProfiles();
                if (pending.Count == 0 && _launching.Count == 0)
                {
                    _canClose = true;
                    _nativeWindow?.Close();
                    return;
                }
                await Task.Delay(250);
            } while (timeout.Elapsed < TimeSpan.FromSeconds(20));

            // A first-start password prompt may still require input. Leave the panel
            // and its console available rather than force-killing the server.
            await DisplayAlertAsync("Панель осталась открыта",
                "Не подтверждена остановка: " + string.Join(", ", pending.Concat(_launching).Distinct())
                + ". Если сервер ждёт ввод при запуске, открой его консоль и заверши настройку.", "OK");
        }
        finally
        {
            _closeInProgress = false;
            if (!_canClose)
                ShowSelectedProfile(_profiles.Selected);
        }
    }

    private async void OnStopServerClicked(object? sender, EventArgs e)
    {
        if (_closeInProgress || _profiles.Selected is not { } profile)
            return;

        if (!await DisplayAlertAsync("Штатная остановка", $"Остановить сервер «{profile.Name}»? Игроки потеряют подключение.",
                "Остановить", "Отмена") || _closeInProgress)
            return;
        _launchMessages.Remove(profile.Name);
        var result = _launches.RequestStop(profile);
        if (result.Error is { } error)
            _launchMessages[profile.Name] = error;
        RefreshStatuses();
    }

    private async void OnStartServerClicked(object? sender, EventArgs e)
    {
        var profile = _profiles.Selected;
        var installation = _savedInstallationFolder;
        if (_closeInProgress || profile is null || installation is null || !_launching.Add(profile.Name))
            return;

        _launchMessages.Remove(profile.Name);
        ShowSelectedProfile(profile);
        try
        {
            var scan = await RefreshExternalServersAsync();
            if (_closeInProgress)
                return;
            if (_savedInstallationFolder != installation)
            {
                _launchMessages[profile.Name] = "Папка установки изменилась. Запусти профиль снова.";
                return;
            }
            if (scan?.GetStatus(profile.Name) is ExternalServerStatus.ProcessObserved or ExternalServerStatus.Ambiguous)
            {
                _launchMessages[profile.Name] = "Внешний процесс уже запущен или не определяется однозначно. Второй запуск не выполнен.";
                return;
            }
            var revision = _pendingSettings.GetValueOrDefault(profile.Name);
            var result = await Task.Run(() => _launches.Start(installation, profile));
            if (!result.Started)
                _launchMessages[profile.Name] = result.Message;
            else
            {
                _settingsAtLaunch[profile.Name] = revision;
                if (_consoleProfile == profile.Name)
                    _consoleTail = null;
            }
        }
        finally
        {
            _launching.Remove(profile.Name);
            RefreshStatuses();
        }
    }

    private async void OnChooseFolderClicked(object? sender, EventArgs e)
    {
        if (Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window owner)
        {
            ShowMessage("Окно ещё не готово. Попробуй выбрать папку снова.");
            return;
        }

        ChooseFolderButton.IsEnabled = false;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
                return;

            var result = _installation.SelectFolder(folder.Path);
            if (result.Accepted)
            {
                RefreshSavedFolder();
                await RefreshExternalServersAsync();
            }

            ShowMessage(result.Error ?? "Папка Dedicated Server сохранена.");
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowMessage("Не удалось открыть или прочитать папку. Проверь доступ и попробуй снова.");
        }
        finally
        {
            ChooseFolderButton.IsEnabled = true;
        }
    }

    private void RefreshSavedFolder()
    {
        var folder = _installation.SavedFolder;
        if (_savedInstallationFolder != folder)
            _external = null;
        _savedInstallationFolder = folder;
        SavedFolderLabel.Text = _savedInstallationFolder ?? "Папка пока не выбрана.";
        if (ProfileDetails.IsVisible)
            ShowSelectedProfile(_profiles.Selected);
    }

    private void ShowMessage(string message)
    {
        MessageLabel.Text = message;
        MessageLabel.IsVisible = true;
    }
}

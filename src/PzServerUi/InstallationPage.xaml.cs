using System.Diagnostics;
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
    private string? _savedInstallationFolder;
    private string? _scanInstallation;
    private Task<ExternalServerScan>? _scanInFlight;
    private ExternalServerScan? _external;
    private bool _updatingProfiles;
    private string? _consoleProfile;
    private bool _sendingConsole;
    private bool _closeInProgress;
    private bool _canClose;
    private Microsoft.UI.Xaml.Window? _nativeWindow;

    public InstallationPage()
    {
        InitializeComponent();

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
        statusTimer.Tick += (_, _) =>
        {
            if (ProfileDetails.IsVisible && _profiles.Selected is { } profile)
                RefreshSessionStatus(profile);
        };
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
        if (ProfileDetails.IsVisible && _profiles.Selected is { } profile)
            RefreshSessionStatus(profile);
        return result;
    }

    private void RefreshProfiles()
    {
        _updatingProfiles = true;
        try
        {
            _profiles.Refresh();
            ProfileList.ItemsSource = _profiles.Items;
            ProfileList.SelectedItem = _profiles.Selected;
            ProfileListMessage.Text = _profiles.Items.Count == 0
                ? $"Профили не найдены. Здесь появятся существующие конфигурации из {_profiles.ConfigurationDirectory}."
                : string.Empty;
            ShowSelectedProfile(_profiles.Selected);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ProfileList.ItemsSource = null;
            ProfileListMessage.Text = $"Не удалось прочитать папку {_profiles.ConfigurationDirectory}. Проверь доступ и обнови список.";
            ShowSelectedProfile(null);
        }
        finally
        {
            ProfileListMessage.IsVisible = !string.IsNullOrEmpty(ProfileListMessage.Text);
            _updatingProfiles = false;
        }
    }

    private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingProfiles || e.CurrentSelection.FirstOrDefault() is not ServerProfile profile)
            return;

        _profiles.Select(profile.Name);
        ShowSelectedProfile(_profiles.Selected);
    }

    private void ShowSelectedProfile(ServerProfile? profile)
    {
        ProfileDetails.IsVisible = profile is not null;
        ProfileDetailsPlaceholder.IsVisible = profile is null;
        ProfileDetailsPlaceholder.Text = "Нет выбранного профиля.";
        if (!string.Equals(_consoleProfile, profile?.Name, StringComparison.OrdinalIgnoreCase))
        {
            _consoleProfile = profile?.Name;
            ConsoleInput.Text = "";
            MaskConsoleInput.IsChecked = true;
            ConsoleOutput.Text = "";
            ConsoleMessage.IsVisible = false;
        }
        if (profile is null)
            return;

        ProfileNameLabel.Text = profile.Name;
        ProfileConfigurationLabel.Text = profile.ConfigurationPath;
        ProfileWorldLabel.Text = profile.WorldPath ?? "Папка мира ещё не найдена.";
        RefreshSessionStatus(profile);
    }

    private void RefreshSessionStatus(ServerProfile profile)
    {
        var status = _launching.Contains(profile.Name)
            ? ServerSessionStatus.Starting
            : _launches.GetStatus(profile);
        var external = _external?.GetStatus(profile.Name) ?? ExternalServerStatus.NotObserved;
        var ownActive = status is ServerSessionStatus.Starting or ServerSessionStatus.Running
            or ServerSessionStatus.Stopping or ServerSessionStatus.StopUnconfirmed;
        SessionStatusLabel.Text = status switch
        {
            ServerSessionStatus.Starting => "Запускается. Готовность не подтверждена.",
            ServerSessionStatus.Running => "Работает",
            ServerSessionStatus.StartupFailed => "Запуск не удался: лаунчер завершился до подтверждения готовности.",
            ServerSessionStatus.Ended => "Лаунчер завершился.",
            ServerSessionStatus.Stopping => "Останавливается. Ожидаем завершения сервера.",
            ServerSessionStatus.Stopped => "Остановлен",
            ServerSessionStatus.StopUnconfirmed => "Не удалось подтвердить остановку. Проверь консоль сервера.",
            _ => "Сеанс не отслеживается.",
        };
        if (!ownActive)
        {
            SessionStatusLabel.Text = external switch
            {
                ExternalServerStatus.ProcessObserved => "Процесс запущен, готовность не подтверждена (внешний сеанс).",
                ExternalServerStatus.Ambiguous => "Внешний сеанс не определён однозначно: несколько процессов с этим именем.",
                _ when status == ServerSessionStatus.NotObserved && _external is { Available: false }
                    => "Не удалось проверить внешние сеансы.",
                _ => SessionStatusLabel.Text,
            };
        }
        StartServerButton.IsEnabled = !_closeInProgress && _savedInstallationFolder is not null
            && !ownActive && external == ExternalServerStatus.NotObserved;
        StopServerButton.IsEnabled = !_closeInProgress && status == ServerSessionStatus.Running;
        LaunchMessageLabel.Text = _launchMessages.GetValueOrDefault(profile.Name)
            ?? (_savedInstallationFolder is null ? "Выбери папку Dedicated Server, чтобы запустить профиль." : "");
        LaunchMessageLabel.IsVisible = LaunchMessageLabel.Text.Length > 0;
        var canSend = !_sendingConsole && !_closeInProgress && !_launching.Contains(profile.Name)
            && status is ServerSessionStatus.Starting or ServerSessionStatus.Running;
        ConsoleInput.IsEnabled = SendConsoleButton.IsEnabled = canSend;
        if (ConsolePane.IsVisible)
        {
            var tail = _launches.ReadConsole(profile);
            if (!string.Equals(ConsoleOutput.Text, tail, StringComparison.Ordinal))
                ConsoleOutput.Text = tail;
        }
    }

    private void OnShowProfileClicked(object? sender, EventArgs e) => SetConsoleVisible(false);
    private void OnShowConsoleClicked(object? sender, EventArgs e) => SetConsoleVisible(true);

    private void SetConsoleVisible(bool visible)
    {
        ConsolePane.IsVisible = ShowProfileButton.IsEnabled = visible;
        ProfilePane.IsVisible = ShowConsoleButton.IsEnabled = !visible;
        if (_profiles.Selected is { } profile)
            RefreshSessionStatus(profile);
    }

    private void OnMaskConsoleChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (ConsoleInput is not null)
            ConsoleInput.IsPassword = e.Value;
    }

    private async void OnSendConsoleClicked(object? sender, EventArgs e)
    {
        if (_closeInProgress || _sendingConsole || _profiles.Selected is not { } profile)
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

    private void OnStopServerClicked(object? sender, EventArgs e)
    {
        if (_closeInProgress || _profiles.Selected is not { } profile)
            return;

        _launchMessages.Remove(profile.Name);
        var result = _launches.RequestStop(profile);
        if (result.Error is { } error)
            _launchMessages[profile.Name] = error;
        RefreshSessionStatus(profile);
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
            var result = await Task.Run(() => _launches.Start(installation, profile));
            if (!result.Started)
                _launchMessages[profile.Name] = result.Message;
        }
        finally
        {
            _launching.Remove(profile.Name);
            if (ProfileDetails.IsVisible)
                ShowSelectedProfile(_profiles.Selected);
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

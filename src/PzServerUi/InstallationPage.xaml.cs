using Microsoft.Maui.Controls;
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
    private bool _updatingProfiles;

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
        Loaded += (_, _) => statusTimer.Start();
        Unloaded += (_, _) => statusTimer.Stop();
    }

    private void OnRefreshProfilesClicked(object? sender, EventArgs e) => RefreshProfiles();

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
        SessionStatusLabel.Text = status switch
        {
            ServerSessionStatus.Starting => "Запускается. Готовность не подтверждена.",
            ServerSessionStatus.Running => "Работает",
            ServerSessionStatus.StartupFailed => "Запуск не удался: лаунчер завершился до подтверждения готовности.",
            ServerSessionStatus.Ended => "Лаунчер завершился.",
            _ => "Сеанс не отслеживается.",
        };
        StartServerButton.IsEnabled = _savedInstallationFolder is not null
            && status is not (ServerSessionStatus.Starting or ServerSessionStatus.Running);
        LaunchMessageLabel.Text = _launchMessages.GetValueOrDefault(profile.Name)
            ?? (_savedInstallationFolder is null ? "Выбери папку Dedicated Server, чтобы запустить профиль." : "");
        LaunchMessageLabel.IsVisible = LaunchMessageLabel.Text.Length > 0;
    }

    private async void OnStartServerClicked(object? sender, EventArgs e)
    {
        var profile = _profiles.Selected;
        var installation = _savedInstallationFolder;
        if (profile is null || installation is null || !_launching.Add(profile.Name))
            return;

        _launchMessages.Remove(profile.Name);
        ShowSelectedProfile(profile);
        try
        {
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
                RefreshSavedFolder();

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
        _savedInstallationFolder = _installation.SavedFolder;
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

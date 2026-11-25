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

    private void RefreshSavedFolder() =>
        SavedFolderLabel.Text = _installation.SavedFolder ?? "Папка пока не выбрана.";

    private void ShowMessage(string message)
    {
        MessageLabel.Text = message;
        MessageLabel.IsVisible = true;
    }
}

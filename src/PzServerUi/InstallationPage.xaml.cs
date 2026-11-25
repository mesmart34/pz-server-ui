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

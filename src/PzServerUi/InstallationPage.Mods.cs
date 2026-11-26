using PzServerUi.Application;

namespace PzServerUi;

public partial class InstallationPage
{
    private string? _modsProfilePath;
    private ServerIniSettings? _modEditSnapshot;

    private void OnAddModClicked(object? sender, EventArgs e)
    {
        if (OpenModSnapshot())
        {
            AddModPanel.IsVisible = true;
            AddModButton.IsVisible = false;
            NewModId.Focus();
        }
    }

    private bool OpenModSnapshot()
    {
        if (_profiles.Selected is not { } profile || profile.ConfigurationPath != _modsProfilePath)
            return false;
        try
        {
            _modEditSnapshot = ServerIniSettings.Open(profile);
            ReloadModIniButton.IsVisible = false;
            ModSaveMessage.IsVisible = false;
            UpdateModValidation();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowModMessage($"Не удалось прочитать INI. {error.Message}");
            return false;
        }
    }

    private void OnModInputChanged(object? sender, TextChangedEventArgs e)
    {
        if (_modEditSnapshot is not null)
            UpdateModValidation();
    }

    private void UpdateModValidation()
    {
        var error = ServerIniSettings.ValidateModIds(NewModId.Text ?? "", NewWorkshopId.Text ?? "");
        ModInputError.Text = error;
        ModInputError.IsVisible = error is not null && (!string.IsNullOrEmpty(NewModId.Text)
            || !string.IsNullOrEmpty(NewWorkshopId.Text));
        SaveModButton.IsEnabled = _modEditSnapshot is not null && error is null;
    }

    private void OnSaveModClicked(object? sender, EventArgs e)
    {
        if (_modEditSnapshot is not { } snapshot || _profiles.Selected is not { } profile
            || profile.ConfigurationPath != _modsProfilePath)
            return;
        try
        {
            var changed = snapshot.AddMod(NewModId.Text ?? "", NewWorkshopId.Text ?? "");
            CloseAddMod();
            ModSearch.Text = "";
            RefreshProfileMods(profile);
            if (changed)
            {
                MarkSettingsSaved();
                if (_iniProfilePath == profile.ConfigurationPath && _iniDraft is { Changes.Count: 0 })
                    LoadIni(profile);
            }
            ShowModMessage(changed ? "Мод добавлен в настройки. Изменения вступят в силу при следующем запуске сервера."
                : "Эти идентификаторы уже указаны в настройках.");
        }
        catch (ProfileConfigurationConflictException error)
        {
            _modEditSnapshot = null;
            UpdateModValidation();
            ReloadModIniButton.IsVisible = true;
            ShowModMessage($"{error.Message} Введённые ID сохранены в форме.");
        }
        catch (ArgumentException error)
        {
            ShowModMessage(error.Message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowModMessage($"Не удалось сохранить INI. {error.Message}");
        }
    }

    private void OnReloadModIniClicked(object? sender, EventArgs e)
    {
        if (OpenModSnapshot())
            RefreshProfileMods(_profiles.Selected);
    }

    private void OnCancelAddModClicked(object? sender, EventArgs e)
    {
        CloseAddMod();
        ModSaveMessage.IsVisible = false;
    }

    private void CloseAddMod()
    {
        _modEditSnapshot = null;
        AddModPanel.IsVisible = ReloadModIniButton.IsVisible = ModInputError.IsVisible = SaveModButton.IsEnabled = false;
        NewModId.Text = NewWorkshopId.Text = "";
        AddModButton.IsVisible = true;
    }

    private void ShowModMessage(string message)
    {
        ModSaveMessage.Text = message;
        ModSaveMessage.IsVisible = true;
    }
}

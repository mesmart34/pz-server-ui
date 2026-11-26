using PzServerUi.Application;

namespace PzServerUi;

public partial class InstallationPage
{
    private static readonly HttpClient WorkshopHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly SteamWorkshopCatalog _workshop = new(WorkshopHttp, Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PzServerUi", "workshop-cards"));
    private readonly Dictionary<string, WorkshopCardTile> _workshopTiles = new(StringComparer.Ordinal);
    private WorkshopCardTile[] _orderedWorkshopTiles = [];
    private WorkshopCardTile[] _visibleWorkshopTiles = [];
    private ServerProfileMods? _modSnapshot;
    private CancellationTokenSource? _workshopLoad;
    private long _workshopGeneration;
    private string? _modsProfilePath;
    private ServerIniSettings? _modEditSnapshot;

    private void RefreshProfileMods(ServerProfile? profile)
    {
        CancelWorkshopLoad();
        if (_modsProfilePath != profile?.ConfigurationPath)
        {
            _modsProfilePath = profile?.ConfigurationPath;
            ModSaveMessage.IsVisible = ModIdsPanel.IsVisible = false;
            CloseAddMod();
        }
        AddModButton.IsEnabled = ModLists.IsVisible = ModsReadError.IsVisible = WorkshopEmptyLabel.IsVisible = false;
        _modSnapshot = null;
        _workshopTiles.Clear();
        _orderedWorkshopTiles = [];
        ApplyModFilter();
        if (profile is null)
            return;

        try
        {
            _modSnapshot = ServerProfileMods.Read(profile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ModsReadError.Text = error is InvalidDataException
                ? $"Не удалось прочитать список модов. {error.Message}"
                : "Не удалось прочитать INI этого профиля. Проверь файл и нажми «Обновить».";
            ModsReadError.IsVisible = true;
            return;
        }

        _orderedWorkshopTiles = _modSnapshot.WorkshopIds.Distinct(StringComparer.Ordinal)
            .Select(id => new WorkshopCardTile(id)).ToArray();
        foreach (var tile in _orderedWorkshopTiles)
            _workshopTiles.Add(tile.Id, tile);

        AddModButton.IsEnabled = ModLists.IsVisible = true;
        ApplyModFilter();
        if (_workshopTiles.Values.Any(tile => tile.CanOpen))
            _ = LoadWorkshopAsync(_workshopTiles.Keys.ToArray());
    }

    private async Task LoadWorkshopAsync(string[] ids)
    {
        using var load = new CancellationTokenSource();
        _workshopLoad = load;
        var generation = _workshopGeneration;
        // Progress posts to the UI thread. A queued callback may outlive a cancelled profile.
        var progress = new Progress<WorkshopCard>(card =>
        {
            if (generation == _workshopGeneration && ModsPane.IsVisible
                && _workshopTiles.TryGetValue(card.Id, out var tile))
            {
                var titleChanged = tile.Title != card.Title;
                tile.Apply(card);
                if (titleChanged && !string.IsNullOrWhiteSpace(ModSearch.Text))
                    ApplyWorkshopFilter();
            }
        });
        try
        {
            await Task.Run(() => _workshop.EnrichAsync(ids, progress, load.Token), load.Token);
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        finally
        {
            if (_workshopLoad == load)
                _workshopLoad = null;
        }
    }

    private void CancelWorkshopLoad()
    {
        _workshopGeneration++;
        _workshopLoad?.Cancel();
        _workshopLoad = null;
    }

    private void OnModSearchChanged(object? sender, TextChangedEventArgs e) => ApplyModFilter();

    private void ApplyModFilter()
    {
        if (_modSnapshot is { } mods)
        {
            var matches = mods.ModIds.Where(id => OverviewQueries.Matches(id, ModSearch.Text)).ToArray();
            ModIdsLabel.Text = mods.ModIds.Count == 0 ? "Не указаны."
                : matches.Length == 0 ? "Нет совпадений." : string.Join('\n', matches);
            var count = string.IsNullOrWhiteSpace(ModSearch.Text)
                ? $"{mods.ModIds.Count}" : $"{matches.Length}/{mods.ModIds.Count}";
            ModIdsButton.Text = $"Mod ID ({count})  {(ModIdsPanel.IsVisible ? "▴" : "▾")}";
        }
        ApplyWorkshopFilter();
    }

    private void ApplyWorkshopFilter()
    {
        var query = ModSearch.Text;
        var matches = string.IsNullOrWhiteSpace(query) ? _orderedWorkshopTiles
            : _orderedWorkshopTiles.Where(tile => OverviewQueries.Matches(tile.Id, query)
                || OverviewQueries.Matches(tile.Title, query)).ToArray();
        if (!matches.SequenceEqual(_visibleWorkshopTiles))
            WorkshopCards.ItemsSource = _visibleWorkshopTiles = matches;
        WorkshopEmptyLabel.IsVisible = _modSnapshot is not null && matches.Length == 0;
        WorkshopEmptyLabel.Text = _workshopTiles.Count == 0
            ? "В профиле нет позиций Steam Workshop." : "В Steam Workshop нет совпадений по поиску.";
    }

    private void OnToggleModIdsClicked(object? sender, EventArgs e)
    {
        ModIdsPanel.IsVisible = !ModIdsPanel.IsVisible;
        ApplyModFilter();
    }

    private void OnWorkshopCardsSizeChanged(object? sender, EventArgs e)
    {
        if (WorkshopCards.Width > 0 && WorkshopCards.ItemsLayout is GridItemsLayout grid)
        {
            var span = Math.Clamp((int)((WorkshopCards.Width - 16 + 10) / 212), 1, 5);
            if (span != grid.Span)
                grid.Span = span;
        }
    }

    private async void OnOpenWorkshopClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { BindingContext: WorkshopCardTile { CanOpen: true } tile })
            return;
        try
        {
            if (await Launcher.Default.TryOpenAsync(new Uri(
                $"https://steamcommunity.com/sharedfiles/filedetails/?id={tile.Id}")))
                return;
            ShowModMessage("Не удалось открыть страницу Steam Workshop.");
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(error);
            ShowModMessage("Не удалось открыть страницу Steam Workshop.");
        }
    }

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

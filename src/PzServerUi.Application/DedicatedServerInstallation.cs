namespace PzServerUi.Application;

public sealed class DedicatedServerInstallation(string selectionFile)
{
    private static readonly string[] Launchers =
        ["StartServer64.bat", "StartServer64_nosteam.bat", "StartServer32.bat"];

    public string? SavedFolder
    {
        get
        {
            try
            {
                return File.ReadAllText(selectionFile);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }
    }

    public InstallationSelection SelectFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return new(false, "Укажи папку Dedicated Server.");

        if (!Directory.Exists(folder))
            return new(false, "Папка не существует или недоступна. Выбери папку установки Dedicated Server.");

        if (!Launchers.Any(launcher => File.Exists(Path.Combine(folder, launcher))))
            return new(false, $"В папке нет файла запуска Dedicated Server ({string.Join(", ", Launchers)}). Выбери папку выделенного сервера, а не игры.");

        string? temporaryFile = null;
        try
        {
            var destination = Path.GetFullPath(selectionFile);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            temporaryFile = $"{destination}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryFile, folder);
            File.Move(temporaryFile, destination, overwrite: true);
            return new(true, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(false, "Не удалось сохранить выбор. Проверь доступ к папке настроек приложения.");
        }
        finally
        {
            if (temporaryFile is not null)
            {
                try
                {
                    File.Delete(temporaryFile);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Cleanup must not obscure the original result.
                }
            }
        }
    }
}

public sealed record InstallationSelection(bool Accepted, string? Error);

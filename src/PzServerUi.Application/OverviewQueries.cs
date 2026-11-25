using System.Text;

namespace PzServerUi.Application;

public enum StatusTone { Neutral, Ready, Attention, Error }
public enum ConsoleLogLevel { All, Log, Warn, Error, Debug, Unknown }
public sealed record OverviewStatus(string Short, string Detail, StatusTone Tone, string? StopHint);

public static class OverviewQueries
{
    public static bool Matches(string value, string? query) =>
        string.IsNullOrWhiteSpace(query) || value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    public static OverviewStatus Describe(ServerSessionStatus session, ExternalServerStatus external, bool scanAvailable)
    {
        return session switch
        {
            ServerSessionStatus.Running => new("Работает", "Работает", StatusTone.Ready, null),
            ServerSessionStatus.Starting => new("Запускается", "Запускается. Готовность не подтверждена.",
                StatusTone.Attention, "Штатная остановка доступна после подтверждения готовности."),
            ServerSessionStatus.Stopping => new("Останавливается", "Останавливается. Ожидаем завершения сервера.",
                StatusTone.Attention, "Запрос остановки уже отправлен."),
            ServerSessionStatus.StopUnconfirmed => new("Остановка не подтверждена",
                "Не удалось подтвердить остановку. Проверь консоль сервера.", StatusTone.Error, "Запрос остановки уже отправлен."),
            _ when external == ExternalServerStatus.ProcessObserved => new("Внешний процесс",
                "Обнаружен внешний процесс. Готовность не подтверждена.", StatusTone.Attention,
                "Панель не управляет сеансами, запущенными вне приложения."),
            _ when external == ExternalServerStatus.Ambiguous => new("Внешний сеанс не определён",
                "Найдено несколько внешних процессов с этим именем.", StatusTone.Attention,
                "Панель не управляет сеансами, запущенными вне приложения."),
            _ when !scanAvailable => Inactive("Проверка недоступна", "Не удалось проверить внешние сеансы.", StatusTone.Attention),
            ServerSessionStatus.StartupFailed => Inactive("Запуск не удался",
                "Лаунчер завершился до подтверждения готовности.", StatusTone.Error),
            ServerSessionStatus.Ended => Inactive("Лаунчер завершился", "Лаунчер завершился без подтверждённой штатной остановки.", StatusTone.Attention),
            ServerSessionStatus.Stopped => Inactive("Остановлен", "Остановлен", StatusTone.Neutral),
            _ => Inactive("Сеанс не обнаружен", "Сеанс не обнаружен.", StatusTone.Neutral)
        };

        static OverviewStatus Inactive(string shortText, string detail, StatusTone tone) =>
            new(shortText, detail, tone, "Нет работающего сеанса, запущенного из панели.");
    }

    public static string FilterLog(string log, string? query, ConsoleLogLevel level)
    {
        if (level == ConsoleLogLevel.All && string.IsNullOrWhiteSpace(query))
            return log;
        using var reader = new StringReader(log);
        var result = new StringBuilder();
        while (reader.ReadLine() is { } line)
        {
            if (!Matches(line, query) || (level != ConsoleLogLevel.All && GetLogLevel(line) != level))
                continue;
            if (result.Length > 0)
                result.AppendLine();
            result.Append(line);
        }
        return result.ToString();
    }

    private static ConsoleLogLevel GetLogLevel(string line)
    {
        var separator = line.IndexOf(':');
        if (separator < 0)
            return ConsoleLogLevel.Unknown;
        return line.AsSpan(0, separator).Trim() switch
        {
            "LOG" => ConsoleLogLevel.Log,
            "WARN" or "WARNING" => ConsoleLogLevel.Warn,
            "ERROR" => ConsoleLogLevel.Error,
            "DEBUG" => ConsoleLogLevel.Debug,
            _ => ConsoleLogLevel.Unknown
        };
    }
}

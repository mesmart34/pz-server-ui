using System.Diagnostics;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace PzServerUi.Application;

public interface IServerLaunchProcess : IDisposable
{
    bool HasExited { get; }
    bool ExitConfirmed { get; }
    bool ReadinessConfirmed { get; }
    void RequestGracefulStop();
    string ReadConsoleTail();
    Task SendConsoleLineAsync(string line);
}

public enum ServerSessionStatus
{
    NotObserved,
    Starting,
    Running,
    StartupFailed,
    Ended,
    Stopping,
    Stopped,
    StopUnconfirmed,
}

public sealed record ServerLaunchResult(bool Started, string Message);
public sealed record ServerStopResult(bool Accepted, string? Error = null);

public sealed class ServerLaunches(Func<ProcessStartInfo, IServerLaunchProcess> startProcess)
{
    private static readonly Regex SafeProfileName = new(
        @"\A[\p{L}\p{N}_][\p{L}\p{M}\p{N} _.-]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex DedicatedCommand = new(
        """^\s*@?\s*(?:"\.\\jre(?:64|32)?\\bin\\java\.exe"|\.\\jre(?:64|32)?\\bin\\java\.exe)\s+.+?\bzombie\.network\.GameServer\b(?<args>.*)$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ArgumentForwarding = new(
        @"(?:^|\s)(?:%\*|%1\s+%2)(?=\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex FixedServerOptions = new(
        @"(?:^|\s|[""=])(?:-servername\b|-cachedir\b|-Duser\.home=)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FixedPortOptions = new(
        @"(?:^|\s|[""=])-(?:port|udpport)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public ServerLaunches() : this(WindowsSessionLauncher.Start) { }

    public string ReadConsole(ServerProfile profile)
    {
        lock (_gate)
            return _sessions.GetValueOrDefault(profile.Name)?.Process?.ReadConsoleTail() ?? "";
    }

    public async Task<string?> SendConsoleLineAsync(ServerProfile profile, string line)
    {
        if (line.Length > 1024 || line.Any(char.IsControl))
            return "Введи одну строку длиной до 1024 символов без управляющих символов.";

        IServerLaunchProcess process;
        lock (_gate)
        {
            if (Observe(profile.Name) is not (ServerSessionStatus.Starting or ServerSessionStatus.Running))
                return "Консоль этого сеанса недоступна.";
            process = _sessions[profile.Name].Process!;
        }

        try
        {
            await process.SendConsoleLineAsync(line);
            return null;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or
            UnauthorizedAccessException or TimeoutException or OperationCanceledException)
        {
            // Do not put the entered command or password into diagnostics.
            return $"Не удалось передать ввод ({error.GetType().Name}, 0x{error.HResult:X8}). Проверь состояние сеанса.";
        }
    }

    public IReadOnlyList<string> PendingShutdownProfiles()
    {
        lock (_gate)
            return _sessions.Keys.Where(name => Observe(name) is ServerSessionStatus.Starting
                or ServerSessionStatus.Running or ServerSessionStatus.Stopping
                or ServerSessionStatus.StopUnconfirmed).ToArray();
    }

    public void RequestShutdownStops()
    {
        lock (_gate)
        {
            foreach (var name in _sessions.Keys)
            {
                // During first launch stdin can be a password prompt rather than a command console.
                if (Observe(name) == ServerSessionStatus.Running)
                    RequestStop(new ServerProfile(name, "", null));
            }
        }
    }

    public ServerStopResult RequestStop(ServerProfile profile)
    {
        lock (_gate)
        {
            if (Observe(profile.Name) != ServerSessionStatus.Running)
                return new(false, "Можно остановить только работающий сеанс, запущенный из панели.");

            var session = _sessions[profile.Name];
            try
            {
                session.Process!.RequestGracefulStop();
                session.Status = ServerSessionStatus.Stopping;
                return new(true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return new(false, $"Не удалось передать запрос остановки: {error.Message}");
            }
        }
    }

    public ServerSessionStatus GetStatus(ServerProfile profile)
    {
        lock (_gate)
            return Observe(profile.Name);
    }

    public ServerLaunchResult Start(string? installationFolder, ServerProfile profile)
    {
        lock (_gate)
        {
            try
            {
                if (Observe(profile.Name) is ServerSessionStatus.Starting or ServerSessionStatus.Running
                    or ServerSessionStatus.Stopping or ServerSessionStatus.StopUnconfirmed)
                    return Reject("Окно запуска этого профиля ещё открыто. Второй запуск не выполнен.");

                if (string.IsNullOrWhiteSpace(installationFolder))
                    return Reject("Сначала выбери папку Dedicated Server.");

                if (!Path.IsPathFullyQualified(installationFolder)
                    || installationFolder.StartsWith(@"\\", StringComparison.Ordinal))
                    return Reject("Для запуска выбери папку Dedicated Server на диске с буквой, не по сетевому пути.");

                if (!File.Exists(profile.ConfigurationPath))
                    return Reject("Файл настроек профиля не найден. Обнови список профилей.");

                if (!SafeProfileName.IsMatch(profile.Name) || profile.Name.EndsWith(' ') || profile.Name.EndsWith('.'))
                    return Reject("Это имя профиля нельзя безопасно передать штатному Windows-лаунчеру.");

                var launcher = DedicatedServerInstallation.FindLauncher(installationFolder);
                if (launcher is null)
                    return Reject("В выбранной установке не найден штатный файл запуска Dedicated Server.");

                if (!ForwardsProfileName(Path.Combine(installationFolder, launcher), out var overridesPorts))
                    return Reject(overridesPorts
                        ? "Лаунчер переопределяет игровые UDP-порты. Панель не может проверить порты из файла настроек. Запуск отменён."
                        : "Панель не смогла подтвердить, что этот лаунчер принимает имя профиля. Запуск отменён.");

                var ports = ServerGamePorts.Read(profile.ConfigurationPath);
                if (FindPortConflict(ports) is { } conflict)
                    return Reject(conflict);

                // The installation path is passed as the working directory, never interpreted by cmd.
                var request = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    WorkingDirectory = installationFolder,
                    Arguments = $"/d /v:off /c .\\{launcher} -servername \"{profile.Name}\"",
                    UseShellExecute = false,
                };
                _sessions[profile.Name] = new(startProcess(request), ports);
                return new(true, "Команда запуска передана. Готовность сервера ещё не подтверждена.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or Win32Exception or InvalidOperationException)
            {
                return Reject($"Не удалось запустить профиль: {error.Message}");
            }
        }
    }

    private string? FindPortConflict(ServerGamePorts ports)
    {
        // Start holds _gate: another click cannot slip in before this session reserves its ports.
        foreach (var (name, session) in _sessions)
        {
            var status = Observe(name);
            if (status is not (ServerSessionStatus.Starting or ServerSessionStatus.Running
                or ServerSessionStatus.Stopping or ServerSessionStatus.StopUnconfirmed))
                continue;
            if (session.Ports.Contains(ports.DefaultPort))
                return $"UDP {ports.DefaultPort} уже используется или зарезервирован профилем «{name}». Запуск отменён.";
            if (session.Ports.Contains(ports.UdpPort))
                return $"UDP {ports.UdpPort} уже используется или зарезервирован профилем «{name}». Запуск отменён.";
        }

        var occupied = ServerGamePorts.ReadOccupiedUdpPorts();
        var conflicts = new[] { ports.DefaultPort, ports.UdpPort }
            .Where(occupied.Contains)
            .Select(port => $"UDP {port}").ToArray();
        return conflicts.Length == 0 ? null
            : $"Заняты локальные порты: {string.Join(", ", conflicts)}. Запуск отменён.";
    }

    private ServerSessionStatus Observe(string profileName)
    {
        if (!_sessions.TryGetValue(profileName, out var session))
            return ServerSessionStatus.NotObserved;

        if (session.Process is { } process)
        {
            var ready = session.Status == ServerSessionStatus.Starting && process.ReadinessConfirmed;
            // Exit wins over a readiness signal which was not observed while the launcher was alive.
            if (process.HasExited)
            {
                session.Process = null;
                session.Status = session.Status switch
                {
                    ServerSessionStatus.Running => ServerSessionStatus.Ended,
                    ServerSessionStatus.Stopping when process.ExitConfirmed => ServerSessionStatus.Stopped,
                    ServerSessionStatus.Stopping => ServerSessionStatus.StopUnconfirmed,
                    _ => ServerSessionStatus.StartupFailed,
                };
                process.Dispose();
            }
            else if (ready)
            {
                session.Status = ServerSessionStatus.Running;
            }
        }
        return session.Status;
    }

    private static bool ForwardsProfileName(string path, out bool overridesPorts)
    {
        overridesPorts = false;
        if (new FileInfo(path).Length > 64 * 1024)
            return false;

        var lines = File.ReadAllLines(path)
            .Select(line => line.TrimStart(' ', '\t', '@'))
            .Where(line => !line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("::", StringComparison.Ordinal));

        var forwards = false;
        foreach (var line in lines)
        {
            if (FixedPortOptions.IsMatch(line))
            {
                overridesPorts = true;
                return false;
            }
            if (FixedServerOptions.IsMatch(line))
                return false;
            if (!line.Contains("zombie.network.GameServer", StringComparison.OrdinalIgnoreCase))
                continue;

            var command = DedicatedCommand.Match(line);
            if (forwards || !command.Success || line.IndexOfAny(['&', '|', '<', '>', '^']) >= 0
                || !ArgumentForwarding.IsMatch(command.Groups["args"].Value))
                return false;

            forwards = true;
        }
        return forwards;
    }

    private static ServerLaunchResult Reject(string message) => new(false, message);

    private sealed class Session(IServerLaunchProcess process, ServerGamePorts ports)
    {
        public IServerLaunchProcess? Process { get; set; } = process;
        public ServerGamePorts Ports { get; } = ports;
        public ServerSessionStatus Status { get; set; } = ServerSessionStatus.Starting;
    }
}

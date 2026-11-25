using System.Diagnostics;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace PzServerUi.Application;

public interface IServerLaunchProcess : IDisposable
{
    bool HasExited { get; }
    bool ExitConfirmed { get; }
    bool TrackingLost { get; }
    bool ReadinessConfirmed { get; }
    void RequestGracefulStop();
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
    TrackingLost,
}

public sealed record ServerLaunchResult(bool Started, string Message);
public sealed record ServerStopResult(bool Accepted, string? Error = null);

public sealed class ServerLaunches
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

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, ProcessStartInfo, IServerLaunchProcess> _startProcess;
    private readonly Func<string, RecoveredServerSession?>? _recover;

    public ServerLaunches() : this(WindowsSessionLauncher.Start, WindowsSessionLauncher.Recover) { }

    public ServerLaunches(Func<ProcessStartInfo, IServerLaunchProcess> startProcess)
        : this((_, request) => startProcess(request), null) { }

    private ServerLaunches(Func<string, ProcessStartInfo, IServerLaunchProcess> startProcess,
        Func<string, RecoveredServerSession?>? recover)
    {
        _startProcess = startProcess;
        _recover = recover;
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
                    or ServerSessionStatus.Stopping or ServerSessionStatus.StopUnconfirmed or ServerSessionStatus.TrackingLost)
                    return Reject("Предыдущий сеанс ещё работает или его завершение не подтверждено. Второй запуск не выполнен.");

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

                if (!ForwardsProfileName(Path.Combine(installationFolder, launcher)))
                    return Reject("Панель не смогла подтвердить, что этот лаунчер принимает имя профиля. Запуск отменён.");

                // The installation path is passed as the working directory, never interpreted by cmd.
                var request = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    WorkingDirectory = installationFolder,
                    Arguments = $"/d /v:off /c .\\{launcher} -servername \"{profile.Name}\"",
                    UseShellExecute = false,
                };
                _sessions[profile.Name] = new(_startProcess(profile.Name, request));
                return new(true, "Команда запуска передана. Готовность сервера ещё не подтверждена.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or Win32Exception or InvalidOperationException)
            {
                return Reject($"Не удалось запустить профиль: {error.Message}");
            }
        }
    }

    private ServerSessionStatus Observe(string profileName)
    {
        if (!_sessions.TryGetValue(profileName, out var session))
        {
            var recovered = _recover?.Invoke(profileName);
            if (recovered is null)
                return ServerSessionStatus.NotObserved;
            if (recovered.Process is null)
                return ServerSessionStatus.TrackingLost;
            _sessions[profileName] = session = new(recovered.Process) { Status = recovered.Status };
        }

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
                if (session.Status != ServerSessionStatus.Stopped && process.TrackingLost)
                    session.Status = ServerSessionStatus.TrackingLost;
                process.Dispose();
            }
            else if (ready)
            {
                session.Status = ServerSessionStatus.Running;
            }
        }
        return session.Status;
    }

    private static bool ForwardsProfileName(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024)
            return false;

        var lines = File.ReadAllLines(path)
            .Select(line => line.TrimStart(' ', '\t', '@'))
            .Where(line => !line.StartsWith("rem ", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("::", StringComparison.Ordinal));

        var forwards = false;
        foreach (var line in lines)
        {
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

    private sealed class Session(IServerLaunchProcess process)
    {
        public IServerLaunchProcess? Process { get; set; } = process;
        public ServerSessionStatus Status { get; set; } = ServerSessionStatus.Starting;
    }
}

internal sealed record RecoveredServerSession(IServerLaunchProcess? Process, ServerSessionStatus Status);

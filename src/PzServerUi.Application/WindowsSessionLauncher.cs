using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PzServerUi.Application;

internal static class WindowsSessionLauncher
{
    // The helper owns an isolated console, the child's input, and the per-launch log.
    // None of those handles belong to the MAUI process.
    private const string ConsoleRelay = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $log = $env:PZ_PANEL_SESSION_LOG
        $quit = $env:PZ_PANEL_SESSION_QUIT
        $exitSignal = $env:PZ_PANEL_SESSION_EXIT
        $record = $env:PZ_PANEL_SESSION_RECORD
        $temporaryRecord = [IO.Path]::ChangeExtension($log, '.json.tmp')
        $lease = [Threading.Mutex]::new($false, $env:PZ_PANEL_PROFILE_MUTEX)
        $hasLease = $false
        $registered = $false
        $childExited = $false
        $child = $null
        $reader = $null
        try {
            try { $hasLease = $lease.WaitOne(0) } catch [Threading.AbandonedMutexException] { $hasLease = $true }
            # A stale ownership record is deliberately not replaced: its child might still be alive.
            if (!$hasLease -or [IO.File]::Exists($record)) { exit 1 }
            $identity = [Diagnostics.Process]::GetCurrentProcess()
            $metadata = @{
                Id = $env:PZ_PANEL_SESSION_ID
                ProcessId = $identity.Id
                StartedAt = $identity.StartTime.ToFileTimeUtc()
            }
            $identity.Dispose()
            [IO.File]::WriteAllText($temporaryRecord, (ConvertTo-Json -Compress -InputObject $metadata))
            [IO.File]::Move($temporaryRecord, $record)
            $registered = $true

            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $env:PZ_PANEL_CMD
            $start.WorkingDirectory = $env:PZ_PANEL_INSTALL
            $start.Arguments = $env:PZ_PANEL_CMD_ARGS + ' > "%PZ_PANEL_SESSION_LOG%" 2>&1'
            $start.UseShellExecute = $false
            $start.RedirectStandardInput = $true
            $child = [Diagnostics.Process]::Start($start)
            $buffer = New-Object char[] 4096
            $inputLine = [Text.StringBuilder]::new()
            $quitSent = $false
            $quitDelivered = $false
            do {
                $exited = $child.HasExited
                try {
                    if ($null -eq $reader) {
                        $stream = [IO.File]::Open($log, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                        $reader = [IO.StreamReader]::new($stream, [Console]::OutputEncoding)
                    }
                    for ($chunk = 0; $chunk -lt 16; $chunk++) {
                        $count = $reader.Read($buffer, 0, $buffer.Length)
                        if ($count -eq 0) { break }
                        [Console]::Write($buffer, 0, $count)
                    }
                } catch [IO.IOException] { }
                if (!$exited -and !$quitSent) {
                    if ([IO.File]::Exists($quit)) {
                        $quitSent = $true
                        try {
                            $child.StandardInput.WriteLine('quit')
                            $child.StandardInput.Flush()
                            $quitDelivered = $true
                        } catch [IO.IOException] { }
                        # EOF also releases the batch file's optional trailing PAUSE.
                        try { $child.StandardInput.Close() } catch [IO.IOException] { }
                    } else {
                        # Relay complete lines from our own console. Do not echo passwords.
                        try {
                            $keys = 0
                            while ($keys -lt 100 -and ![Console]::IsInputRedirected -and [Console]::KeyAvailable) {
                                $keys++
                                $key = [Console]::ReadKey($true)
                                if ($key.Key -eq [ConsoleKey]::Enter) {
                                    $child.StandardInput.WriteLine($inputLine.ToString())
                                    $null = $inputLine.Clear()
                                } elseif ($key.Key -eq [ConsoleKey]::Backspace -and $inputLine.Length -gt 0) {
                                    $inputLine.Length--
                                } elseif (![char]::IsControl($key.KeyChar) -and $inputLine.Length -lt 4096) {
                                    $null = $inputLine.Append($key.KeyChar)
                                }
                            }
                        } catch [IO.IOException] { } catch [InvalidOperationException] { }
                    }
                }
                if (!$exited) { Start-Sleep -Milliseconds 75 }
            } until ($exited)
            $childExited = $true
            if ($quitDelivered) { [IO.File]::WriteAllText($exitSignal, '') }
            exit $child.ExitCode
        } finally {
            if ($null -ne $reader) { $reader.Dispose() }
            if ($null -ne $child) { $child.Dispose() }
            if (!$registered -or $null -eq $child -or $childExited) {
                if ($registered) { Remove-Item -LiteralPath $record -ErrorAction SilentlyContinue }
                Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $quit -ErrorAction SilentlyContinue
            }
            Remove-Item -LiteralPath $temporaryRecord -ErrorAction SilentlyContinue
            if ($hasLease) { $lease.ReleaseMutex() }
            $lease.Dispose()
        }
        """;

    public static IServerLaunchProcess Start(string profile, ProcessStartInfo launcher)
    {
        var sessionDirectory = SessionDirectory();
        Directory.CreateDirectory(sessionDirectory);
        var id = Guid.NewGuid().ToString("N");
        var files = new SessionFiles(sessionDirectory, id, RegistryPath(sessionDirectory, profile));
        using (File.Open(files.Log, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
        }

        try
        {
            var helper = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = launcher.WorkingDirectory,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-EncodedCommand",
                         Convert.ToBase64String(Encoding.Unicode.GetBytes(ConsoleRelay)) })
                helper.ArgumentList.Add(arg);
            helper.Environment["PZ_PANEL_SESSION_LOG"] = files.Log;
            helper.Environment["PZ_PANEL_SESSION_QUIT"] = files.Quit;
            helper.Environment["PZ_PANEL_SESSION_EXIT"] = files.Exit;
            helper.Environment["PZ_PANEL_SESSION_ID"] = id;
            helper.Environment["PZ_PANEL_SESSION_RECORD"] = files.Record;
            helper.Environment["PZ_PANEL_PROFILE_MUTEX"] = $@"Local\PzServerUi-{Digest(files.Record.ToUpperInvariant())}";
            helper.Environment["PZ_PANEL_CMD"] = launcher.FileName;
            helper.Environment["PZ_PANEL_INSTALL"] = launcher.WorkingDirectory;
            helper.Environment["PZ_PANEL_CMD_ARGS"] = launcher.Arguments;

            var process = WindowsConsoleProcess.Start(helper);
            return new WindowsLaunchProcess(process, files);
        }
        catch
        {
            try { File.Delete(files.Log); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static RecoveredServerSession? Recover(string profile)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        Process? process = null;
        string? path = null;
        try
        {
            var directory = SessionDirectory();
            path = RegistryPath(directory, profile);
            var record = ReadRecord(path);
            if (record is null)
                return null;
            if (record.ProcessId <= 0 || record.StartedAt <= 0 || !Guid.TryParseExact(record.Id, "N", out _))
                return new(null, ServerSessionStatus.TrackingLost);
            process = Process.GetProcessById(record.ProcessId);
            if (process.StartTime.ToFileTimeUtc() != record.StartedAt || process.HasExited)
                return File.Exists(path) ? new(null, ServerSessionStatus.TrackingLost) : null;

            var files = new SessionFiles(directory, record.Id, path);
            var handle = new WindowsLaunchProcess(process, files);
            var status = File.Exists(files.Quit) ? ServerSessionStatus.Stopping : ServerSessionStatus.Starting;
            process = null; // The returned handle now owns this process reference.
            return new(handle, status);
        }
        catch (ArgumentException) when (path is not null)
        {
            // PID no longer exists. It may have exited normally while we were reading the record.
            return File.Exists(path) ? new(null, ServerSessionStatus.TrackingLost) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or Win32Exception or InvalidOperationException)
        {
            return new(null, ServerSessionStatus.TrackingLost);
        }
        finally { process?.Dispose(); }
    }

    private static OwnershipRecord? ReadRecord(string path)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is < 1 or > 4096)
                throw new IOException("Недопустимый размер записи сеанса.");
            return JsonSerializer.Deserialize<OwnershipRecord>(file)
                ?? throw new JsonException("Пустая запись сеанса.");
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string SessionDirectory()
    {
        var appData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(appData))
            throw new IOException("Не удалось определить локальную папку сеансов приложения.");
        return Path.Combine(appData, "PzServerUi", "sessions");
    }

    private static string RegistryPath(string directory, string profile) =>
        Path.Combine(directory, $"{Digest(profile.ToUpperInvariant())}.session.json");

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record OwnershipRecord(string Id, int ProcessId, long StartedAt);
    private sealed record SessionFiles(string Directory, string Id, string Record)
    {
        public string Log => Path.Combine(Directory, $"{Id}.log");
        public string Quit => Path.Combine(Directory, $"{Id}.quit");
        public string Exit => Path.Combine(Directory, $"{Id}.exited");
    }

    private sealed class WindowsLaunchProcess(Process process, SessionFiles files) : IServerLaunchProcess
    {
        private readonly ReadinessLog _log = new(files.Log);

        public bool HasExited => process.HasExited;
        public bool ExitConfirmed => File.Exists(files.Exit);
        public bool ReadinessConfirmed => _log.HasMarker();
        public bool TrackingLost
        {
            get
            {
                try { return ReadRecord(files.Record)?.Id == files.Id; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                {
                    return true;
                }
            }
        }

        public void RequestGracefulStop()
        {
            if (process.HasExited)
                throw new InvalidOperationException("Лаунчер уже завершился.");
            using var request = File.Open(files.Quit, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }

        public void Dispose()
        {
            process.Dispose();
            foreach (var path in new[] { files.Quit, files.Exit })
            {
                try { File.Delete(path); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class ReadinessLog(string path)
    {
        private static readonly Regex Marker = new(
            @"\ALOG\s*:\s*Network[ ,]+[^>]*>\s*\*\*\* SERVER STARTED \*\*\*\*\s*\z",
            RegexOptions.CultureInvariant);
        private readonly byte[] _line = new byte[4096];
        private int _length;
        private bool _skipLine;
        private long _position;

        public bool HasMarker()
        {
            try
            {
                using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                file.Position = _position;
                Span<byte> buffer = stackalloc byte[4096];
                for (var chunk = 0; chunk < 16; chunk++)
                {
                    var read = file.Read(buffer);
                    if (read == 0)
                        break;
                    _position += read;
                    foreach (var next in buffer[..read])
                    {
                        if (next == (byte)'\n')
                        {
                            var matched = !_skipLine && Marker.IsMatch(Encoding.UTF8.GetString(_line, 0, _length));
                            _length = 0;
                            _skipLine = false;
                            if (matched)
                                return true;
                        }
                        else if (_length < _line.Length)
                            _line[_length++] = next;
                        else
                            _skipLine = true;
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The writer may not have opened the log yet, or may be closing it.
            }
            return false;
        }
    }
}

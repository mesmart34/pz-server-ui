using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PzServerUi.Application;

internal static class WindowsSessionLauncher
{
    // The headless relay owns the child's input. Commands travel in memory through
    // its private redirected stdin; only child output is written to the session log.
    private const string ConsoleRelay = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $log = $env:PZ_PANEL_SESSION_LOG
        $quit = $env:PZ_PANEL_SESSION_QUIT
        $exitSignal = $env:PZ_PANEL_SESSION_EXIT
        $child = $null
        $commands = $null
        try {
            $commands = [IO.StreamReader]::new([Console]::OpenStandardInput(), [Text.Encoding]::UTF8)
            $line = $commands.ReadLineAsync()
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $env:PZ_PANEL_CMD
            $start.WorkingDirectory = $env:PZ_PANEL_INSTALL
            $start.Arguments = $env:PZ_PANEL_CMD_ARGS + ' > "%PZ_PANEL_SESSION_LOG%" 2>&1'
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardInput = $true
            $child = [Diagnostics.Process]::Start($start)
            $quitSent = $false
            $quitDelivered = $false
            do {
                $exited = $child.HasExited
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
                    } elseif ($null -ne $line -and $line.IsCompleted) {
                        try {
                            $inputValue = $line.GetAwaiter().GetResult()
                        } catch [IO.IOException] { $inputValue = $null }
                        $line = $null
                        if ($null -ne $inputValue) {
                            try {
                                if ($inputValue.Length -le 1024) {
                                    $child.StandardInput.WriteLine($inputValue)
                                    $child.StandardInput.Flush()
                                }
                            } catch [IO.IOException] { }
                            $inputValue = $null
                            $line = $commands.ReadLineAsync()
                        }
                    }
                }
                if (!$exited) { Start-Sleep -Milliseconds 75 }
            } until ($exited)
            if ($quitDelivered) { [IO.File]::WriteAllText($exitSignal, '') }
            exit $child.ExitCode
        } finally {
            if ($null -ne $commands) { $commands.Dispose() }
            if ($null -ne $child) { $child.Dispose() }
            Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $quit -ErrorAction SilentlyContinue
        }
        """;

    public static IServerLaunchProcess Start(ProcessStartInfo launcher)
    {
        var appData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(appData))
            appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(appData))
            throw new IOException("Не удалось определить локальную папку журналов приложения.");

        var sessionDirectory = Path.Combine(appData, "PzServerUi", "sessions");
        Directory.CreateDirectory(sessionDirectory);
        var log = Path.Combine(sessionDirectory, $"{Guid.NewGuid():N}.log");
        var quit = Path.ChangeExtension(log, ".quit");
        var exitSignal = Path.ChangeExtension(log, ".exited");
        using (File.Open(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
        }

        try
        {
            var helper = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                WorkingDirectory = launcher.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                         Convert.ToBase64String(Encoding.Unicode.GetBytes(ConsoleRelay)) })
                helper.ArgumentList.Add(arg);
            helper.Environment["PZ_PANEL_SESSION_LOG"] = log;
            helper.Environment["PZ_PANEL_SESSION_QUIT"] = quit;
            helper.Environment["PZ_PANEL_SESSION_EXIT"] = exitSignal;
            helper.Environment["PZ_PANEL_CMD"] = launcher.FileName;
            helper.Environment["PZ_PANEL_INSTALL"] = launcher.WorkingDirectory;
            helper.Environment["PZ_PANEL_CMD_ARGS"] = launcher.Arguments;

            var process = Process.Start(helper) ?? throw new InvalidOperationException("Посредник не был запущен.");
            return new WindowsLaunchProcess(process, log, quit, exitSignal);
        }
        catch
        {
            try { File.Delete(log); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private sealed class WindowsLaunchProcess(Process process, string log, string quit, string exitSignal)
        : IServerLaunchProcess
    {
        private readonly ReadinessLog _readiness = new(log);
        public bool HasExited => process.HasExited;
        public bool ExitConfirmed => File.Exists(exitSignal);
        public bool ReadinessConfirmed => _readiness.HasMarker();

        public string ReadConsoleTail()
        {
            try
            {
                using var stream = File.Open(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var total = stream.Length;
                var length = Math.Min(total, 24 * 1024);
                stream.Position = total - length;
                var bytes = new byte[(int)length];
                var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                return Encoding.UTF8.GetString(bytes, 0, count);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
        }

        public async Task SendConsoleLineAsync(string line)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.StandardInput.WriteLineAsync(line.AsMemory(), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
        }
        public void RequestGracefulStop()
        {
            if (process.HasExited)
                throw new InvalidOperationException("Лаунчер уже завершился.");
            using var request = File.Open(quit, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }

        public void Dispose()
        {
            process.Dispose();
            foreach (var path in new[] { quit, exitSignal })
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

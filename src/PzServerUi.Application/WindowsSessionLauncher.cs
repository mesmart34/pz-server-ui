using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PzServerUi.Application;

internal static class WindowsSessionLauncher
{
    // This helper lives in the server console, not in the MAUI process. cmd keeps its
    // interactive input; the helper mirrors its output from a per-launch file.
    private const string ConsoleRelay = """
        $ErrorActionPreference = 'Stop'
        $log = $env:PZ_PANEL_SESSION_LOG
        $child = $null
        $reader = $null
        try {
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = $env:PZ_PANEL_CMD
            $start.WorkingDirectory = $env:PZ_PANEL_INSTALL
            $start.Arguments = $env:PZ_PANEL_CMD_ARGS + ' > "%PZ_PANEL_SESSION_LOG%" 2>&1'
            $start.UseShellExecute = $false
            $child = [Diagnostics.Process]::Start($start)
            $buffer = New-Object char[] 4096
            do {
                $exited = $child.HasExited
                try {
                    if ($null -eq $reader) {
                        $stream = [IO.File]::Open($log, 'Open', 'Read', ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
                        $reader = [IO.StreamReader]::new($stream, [Console]::OutputEncoding)
                    }
                    do {
                        $count = $reader.Read($buffer, 0, $buffer.Length)
                        if ($count -gt 0) { [Console]::Write($buffer, 0, $count) }
                    } while ($count -gt 0)
                } catch [IO.IOException] { }
                if (!$exited) { Start-Sleep -Milliseconds 75 }
            } until ($exited)
            exit $child.ExitCode
        } finally {
            if ($null -ne $reader) { $reader.Dispose() }
            if ($null -ne $child) { $child.Dispose() }
            Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
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
            };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-EncodedCommand",
                         Convert.ToBase64String(Encoding.Unicode.GetBytes(ConsoleRelay)) })
                helper.ArgumentList.Add(arg);
            helper.Environment["PZ_PANEL_SESSION_LOG"] = log;
            helper.Environment["PZ_PANEL_CMD"] = launcher.FileName;
            helper.Environment["PZ_PANEL_INSTALL"] = launcher.WorkingDirectory;
            helper.Environment["PZ_PANEL_CMD_ARGS"] = launcher.Arguments;

            var process = Process.Start(helper) ?? throw new InvalidOperationException("Процесс запуска не создан.");
            return new WindowsLaunchProcess(process, new ReadinessLog(log));
        }
        catch
        {
            try { File.Delete(log); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private sealed class WindowsLaunchProcess(Process process, ReadinessLog log) : IServerLaunchProcess
    {
        public bool HasExited => process.HasExited;
        public bool ReadinessConfirmed => log.HasMarker();
        public void Dispose() => process.Dispose();
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

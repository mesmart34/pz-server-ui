using System.Diagnostics;
using PzServerUi.Application;

using var sandbox = new TestDirectory();
Environment.SetEnvironmentVariable("LOCALAPPDATA", sandbox.Path);
var folder = Directory.CreateDirectory(Path.Combine(sandbox.Path, "Dedicated Server")).FullName;
File.WriteAllText(Path.Combine(folder, "StartServer64.bat"), "@echo off\r\n");
var selectionFile = Path.Combine(sandbox.Path, "settings", "dedicated-installation.txt");

var selection = new DedicatedServerInstallation(selectionFile);
Check(selection.SavedFolder is null, "A new panel must not start with a selected installation.");
Check(selection.SelectFolder(folder).Accepted, "The folder with the dedicated launcher should be accepted.");
Check(new DedicatedServerInstallation(selectionFile).SavedFolder == folder,
    "A reopened panel must see the accepted installation.");
Console.WriteLine("PASS: accepts and restores a Dedicated Server installation");

var client = Directory.CreateDirectory(Path.Combine(sandbox.Path, "Game client")).FullName;
File.WriteAllText(Path.Combine(client, "ProjectZomboid64.exe"), "");
var rejection = selection.SelectFolder(client);
Check(!rejection.Accepted && !string.IsNullOrWhiteSpace(rejection.Error),
    "A folder containing only the game client must be rejected with an explanation.");
Check(new DedicatedServerInstallation(selectionFile).SavedFolder == folder,
    "Choosing an invalid folder must not replace the saved installation.");
Console.WriteLine("PASS: rejects an invalid folder and keeps the previously saved installation");

foreach (var invalid in new[] { null, "", Path.Combine(sandbox.Path, "Missing folder") })
{
    Check(!selection.SelectFolder(invalid).Accepted, "An empty or missing folder must be rejected.");
    Check(selection.SavedFolder == folder, "An empty or missing folder must not change the selection.");
}
Console.WriteLine("PASS: rejects empty and missing paths");

foreach (var launcher in new[] { "StartServer64_nosteam.bat", "StartServer32.bat" })
{
    var alternative = Directory.CreateDirectory(Path.Combine(sandbox.Path, launcher + " install")).FullName;
    File.WriteAllText(Path.Combine(alternative, launcher), "");
    Check(selection.SelectFolder(alternative).Accepted, $"A folder containing {launcher} must be accepted.");
    Check(new DedicatedServerInstallation(selectionFile).SavedFolder == alternative,
        "Choosing another valid installation must update the saved selection.");
}
Console.WriteLine("PASS: accepts supported alternate launchers and updates the selection");

if (OperatingSystem.IsWindows())
{
    var beforeFailedSave = selection.SavedFolder;
    using (File.Open(selectionFile, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        var failedSave = selection.SelectFolder(folder);
        Check(!failedSave.Accepted && !string.IsNullOrWhiteSpace(failedSave.Error),
            "A locked settings file must produce a save error with an explanation.");
        Check(selection.SavedFolder == beforeFailedSave, "A failed save must preserve the previous choice.");
    }
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(selectionFile)!, "*.tmp").Any(),
        "A failed save should not leave temporary files in the settings folder.");
    Console.WriteLine("PASS: a locked settings file preserves the previous installation");
}

var data = Path.Combine(sandbox.Path, "Zomboid");
var profiles = new ServerProfiles(data);
profiles.Refresh();
Check(profiles.Items.Count == 0 && profiles.Selected is null, "Missing data should show an empty list.");
Check(!Directory.Exists(data), "Discovery must not create the game's data folder.");

var configDirectory = Directory.CreateDirectory(Path.Combine(data, "Server")).FullName;
profiles.Refresh();
Check(profiles.Items.Count == 0 && profiles.Selected is null, "An empty Server folder should show an empty list.");

var alphaConfig = Path.Combine(configDirectory, "alpha.ini");
var bravoConfig = Path.Combine(configDirectory, "bravo.INI");
File.WriteAllText(alphaConfig, "PublicName=Alpha\n");
File.WriteAllText(bravoConfig, "PublicName=Bravo\n");
File.WriteAllText(Path.Combine(configDirectory, "orphan_SandboxVars.lua"), "");
var world = Directory.CreateDirectory(Path.Combine(data, "Saves", "Multiplayer", "alpha")).FullName;
var worldFile = Path.Combine(world, "map.bin");
File.WriteAllText(worldFile, "world");
var beforeScan = Directory.EnumerateFileSystemEntries(data, "*", SearchOption.AllDirectories).Order().ToArray();

profiles.Refresh();
Check(profiles.Items.Select(profile => profile.Name).SequenceEqual(new[] { "alpha", "bravo" }),
    "All named INI profiles should appear in name order, even without a world.");
Check(profiles.Selected is { Name: "alpha" } && profiles.Selected.WorldPath == world,
    "On opening the panel, details should show the first profile and its existing world.");
profiles.Select("bravo");
Check(profiles.Selected is { Name: "bravo", WorldPath: null } && profiles.Selected.ConfigurationPath == bravoConfig,
    "Selecting another profile must change the details to that profile, without requiring a world.");
profiles.Refresh();
Check(profiles.Selected?.Name == "bravo", "Refreshing should preserve an existing selection.");
Check(Directory.EnumerateFileSystemEntries(data, "*", SearchOption.AllDirectories).Order().SequenceEqual(beforeScan)
    && File.ReadAllText(alphaConfig) == "PublicName=Alpha\n"
    && File.ReadAllText(bravoConfig) == "PublicName=Bravo\n"
    && File.ReadAllText(worldFile) == "world", "Discovery and selection must not modify configs or worlds.");

File.Delete(bravoConfig);
profiles.Refresh();
Check(profiles.Selected?.Name == "alpha", "Refreshing after the selected profile is removed must not keep stale details.");
Console.WriteLine("PASS: discovers named profiles without modifying data and keeps details in sync with selection");

var launchFolder = Directory.CreateDirectory(Path.Combine(sandbox.Path, "Server Install & Tools")).FullName;
var launcherPath = Path.Combine(launchFolder, "StartServer64.bat");
const string forwardingScript = "@echo off\r\n\".\\jre64\\bin\\java.exe\" -Xmx2g zombie.network.GameServer -statistic 0 %1 %2\r\n";
File.WriteAllText(launcherPath, forwardingScript);
var namedConfig = Path.Combine(configDirectory, "Second world.ini");
File.WriteAllText(namedConfig, "PublicName=Second\n");
profiles.Refresh();
profiles.Select("Second world");
var namedProfile = profiles.Selected!;
var requests = new List<ProcessStartInfo>();
var processes = new List<FakeLaunchProcess>();
var launches = new ServerLaunches(request =>
{
    requests.Add(request);
    var process = new FakeLaunchProcess();
    processes.Add(process);
    return process;
});

Check(launches.Start(launchFolder, namedProfile).Started, "A compatible dedicated launcher should start the selected profile.");
Check(requests.Count == 1 && Path.GetFileName(requests[0].FileName) == "cmd.exe"
    && requests[0].WorkingDirectory == launchFolder
    && requests[0].Arguments == "/d /v:off /c .\\StartServer64.bat -servername \"Second world\"",
    "The exact selected name should be sent to the vendor launcher in the selected installation.");
Check(!launches.Start(launchFolder, namedProfile).Started && requests.Count == 1,
    "Repeating the same launch must not start a second process while the launcher is active.");
profiles.Select("alpha");
Check(launches.Start(launchFolder, profiles.Selected!).Started && requests.Count == 2,
    "A different selected profile must not be blocked by another profile's launch.");
processes[0].HasExited = true;
Check(launches.Start(launchFolder, namedProfile).Started && requests.Count == 3,
    "A profile may launch again once its prior launcher has exited.");
Check(File.ReadAllText(launcherPath) == forwardingScript
    && File.ReadAllText(alphaConfig) == "PublicName=Alpha\n"
    && File.ReadAllText(namedConfig) == "PublicName=Second\n", "Launching must not rewrite scripts or profiles.");
Console.WriteLine("PASS: routes a dedicated launch to the selected profile and prevents duplicate requests");

processes[2].HasExited = true;
File.WriteAllText(launcherPath, forwardingScript.Replace(" %1 %2", ""));
Check(!launches.Start(launchFolder, namedProfile).Started && requests.Count == 3,
    "A launcher that ignores the profile argument must not fall back to the default profile.");
File.WriteAllText(launcherPath, forwardingScript.Replace(" %1 %2", " -servername servertest %1 %2"));
Check(!launches.Start(launchFolder, namedProfile).Started && requests.Count == 3,
    "A launcher with a hard-coded profile must not be started.");
File.WriteAllText(launcherPath, forwardingScript);
var unsafePath = Path.Combine(configDirectory, "risk%TEMP%.ini");
File.WriteAllText(unsafePath, "");
Check(!launches.Start(launchFolder, new ServerProfile("risk%TEMP%", unsafePath, null)).Started && requests.Count == 3,
    "A profile containing cmd expansion characters must not be passed to the shell.");
Check(!launches.Start(null, namedProfile).Started && requests.Count == 3,
    "Without a selected installation, the launch must not choose any fallback executable.");
if (OperatingSystem.IsWindows())
{
    var unc = launches.Start(@"\\example\servers", namedProfile);
    Check(!unc.Started && unc.Message.Contains("сетевому", StringComparison.Ordinal) && requests.Count == 3,
        "A UNC working directory must be rejected before invoking cmd.");
}
Console.WriteLine("PASS: refuses incompatible launchers and unsafe or incomplete requests");

if (OperatingSystem.IsWindows())
{
    var probeDirectory = Directory.CreateDirectory(Path.Combine(sandbox.Path, "Argument probe & spaces")).FullName;
    File.WriteAllText(Path.Combine(probeDirectory, "StartServer64.bat"),
        "@echo off\r\necho %~1\r\necho %~2\r\n");
    using var probe = Process.Start(new ProcessStartInfo
    {
        FileName = requests[0].FileName,
        Arguments = requests[0].Arguments,
        WorkingDirectory = probeDirectory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
    }) ?? throw new InvalidOperationException("The Windows argument probe did not start.");
    var received = await probe.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
    await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    Check(probe.ExitCode == 0 && received.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
        .SequenceEqual(new[] { "-servername", "Second world" }),
        "Windows cmd must deliver the full name as one argument, without interpreting the installation path.");
    Console.WriteLine("PASS: Windows batch argument probe preserves a profile name with spaces");
}

var sessionProcesses = new List<FakeLaunchProcess>();
var sessions = new ServerLaunches(_ =>
{
    var process = new FakeLaunchProcess();
    sessionProcesses.Add(process);
    return process;
});
var alphaProfile = profiles.Items.Single(item => item.Name == "alpha");
Check(sessions.GetStatus(namedProfile) == ServerSessionStatus.NotObserved,
    "An undiscovered session must not be reported as stopped or running.");
Check(sessions.Start(launchFolder, namedProfile).Started, "The first test session should launch.");
Check(sessions.GetStatus(namedProfile) == ServerSessionStatus.Starting,
    "A process existing must not be enough to mark the profile running.");
Check(sessions.Start(launchFolder, alphaProfile).Started, "A second profile should launch independently.");
sessionProcesses[1].ReadinessConfirmed = true;
Check(sessions.GetStatus(alphaProfile) == ServerSessionStatus.Running
    && sessions.GetStatus(namedProfile) == ServerSessionStatus.Starting,
    "A readiness confirmation must apply only to the session it came from.");

sessionProcesses[0].HasExited = true;
Check(sessions.GetStatus(namedProfile) == ServerSessionStatus.StartupFailed,
    "A launcher exit before confirmation must be visible as startup failure.");
sessionProcesses[1].HasExited = true;
Check(sessions.GetStatus(alphaProfile) == ServerSessionStatus.Ended,
    "An already confirmed session should not be reported as a failed startup when its launcher exits.");
Check(sessions.Start(launchFolder, alphaProfile).Started
    && sessions.GetStatus(alphaProfile) == ServerSessionStatus.Starting,
    "A new launch must not inherit readiness from the previous session of the same profile.");
sessionProcesses[2].HasExited = true;
sessionProcesses[2].ReadinessConfirmed = true;
Check(sessions.GetStatus(alphaProfile) == ServerSessionStatus.StartupFailed,
    "A readiness signal must not turn an already exited launcher into a running session.");
Console.WriteLine("PASS: observes startup and exit by session, never borrowing or reusing readiness");

Check(sessions.Start(launchFolder, namedProfile).Started && sessions.Start(launchFolder, alphaProfile).Started,
    "Two independently tracked profiles should start.");
var target = sessionProcesses[^2];
var neighbor = sessionProcesses[^1];
target.ReadinessConfirmed = neighbor.ReadinessConfirmed = true;
Check(sessions.RequestStop(namedProfile).Accepted && target.StopRequests == 1 && neighbor.StopRequests == 0,
    "A graceful stop must be sent only to the selected owned session.");
Check(sessions.GetStatus(namedProfile) == ServerSessionStatus.Stopping
    && sessions.GetStatus(alphaProfile) == ServerSessionStatus.Running
    && !sessions.RequestStop(namedProfile).Accepted && target.StopRequests == 1
    && !sessions.Start(launchFolder, namedProfile).Started,
    "An accepted stop is pending and may not be repeatedly sent while the process is alive.");
target.HasExited = true;
Check(sessions.GetStatus(namedProfile) == ServerSessionStatus.Stopped
    && sessions.GetStatus(alphaProfile) == ServerSessionStatus.Running,
    "A requested stop is confirmed only by the selected process exiting.");
Check(!sessions.RequestStop(namedProfile).Accepted, "An exited process must not receive a stop command.");
Check(sessions.Start(launchFolder, namedProfile).Started
    && sessions.GetStatus(namedProfile) == ServerSessionStatus.Starting,
    "The same profile can restart after confirmed exit without reusing old session state.");
neighbor.ExitConfirmed = false;
Check(sessions.RequestStop(alphaProfile).Accepted, "The second running session should now accept its own request.");
neighbor.HasExited = true;
Check(sessions.GetStatus(alphaProfile) == ServerSessionStatus.StopUnconfirmed
    && !sessions.Start(launchFolder, alphaProfile).Started,
    "Unexpected relay exit must not be called a graceful stop or allow a duplicate start.");
Console.WriteLine("PASS: requests a graceful stop only for the selected session and observes its exit");

var shutdownProcesses = new List<FakeLaunchProcess>();
var shutdown = new ServerLaunches(_ =>
{
    var process = new FakeLaunchProcess();
    shutdownProcesses.Add(process);
    return process;
});
Check(shutdown.Start(launchFolder, namedProfile).Started && shutdown.Start(launchFolder, alphaProfile).Started,
    "Both profiles should start before the application exit test.");
shutdownProcesses[0].ConsoleText = "first startup prompt";
shutdownProcesses[1].ConsoleText = "second startup prompt";
Check(shutdown.ReadConsole(namedProfile) == "first startup prompt"
    && shutdown.ReadConsole(alphaProfile) == "second startup prompt"
    && await shutdown.SendConsoleLineAsync(namedProfile, "startup-response") is null
    && shutdownProcesses[0].Received.SequenceEqual(new[] { "startup-response" })
    && shutdownProcesses[1].Received.Count == 0
    && await shutdown.SendConsoleLineAsync(alphaProfile, "one\ntwo") is not null,
    "The build must route one line only to the selected session, including before readiness.");
shutdownProcesses[0].ReadinessConfirmed = true;
shutdown.RequestShutdownStops();
Check(shutdownProcesses[0].StopRequests == 1 && shutdownProcesses[1].StopRequests == 0
    && shutdown.PendingShutdownProfiles().Count == 2,
    "Shutdown sends quit to ready sessions and waits for starting sessions without entering quit as a password.");
shutdownProcesses[1].ReadinessConfirmed = true;
shutdown.RequestShutdownStops();
Check(shutdownProcesses.All(p => p.StopRequests == 1), "Every owned ready session gets precisely one shutdown request.");
shutdownProcesses[0].HasExited = true;
Check(shutdown.PendingShutdownProfiles().SequenceEqual(new[] { alphaProfile.Name }),
    "The panel must not close while any owned session is still stopping.");
shutdownProcesses[1].HasExited = true;
Check(shutdown.PendingShutdownProfiles().Count == 0, "The panel can close only after both sessions exit.");
Console.WriteLine("PASS: console input is profile-scoped and confirmed app exit waits for all owned sessions");

if (OperatingSystem.IsWindows())
{
    var exitProbeDirectory = Directory.CreateDirectory(Path.Combine(sandbox.Path, "Early exit probe")).FullName;
    File.WriteAllText(Path.Combine(exitProbeDirectory, "StartServer64.bat"), "@echo off\r\nexit /b 7\r\n"
        + forwardingScript);
    var windowsSessions = new ServerLaunches();
    Check(windowsSessions.Start(exitProbeDirectory, namedProfile).Started,
        "The harmless Windows launcher fixture should start a cmd process.");
    var timeout = Stopwatch.StartNew();
    while (windowsSessions.GetStatus(namedProfile) == ServerSessionStatus.Starting
        && timeout.Elapsed < TimeSpan.FromSeconds(10))
        await Task.Delay(25);
    Check(windowsSessions.GetStatus(namedProfile) == ServerSessionStatus.StartupFailed,
        "A real cmd exit must be reported as failed startup without ever declaring readiness.");
    Console.WriteLine("PASS: Windows launcher exit is reported without starting a game process");

    var readyProbeDirectory = Directory.CreateDirectory(Path.Combine(sandbox.Path, "Readiness probe & spaces")).FullName;
    var generalFile = Path.Combine(readyProbeDirectory, "general.flag");
    var releaseFile = Path.Combine(readyProbeDirectory, "release.flag");
    File.WriteAllText(Path.Combine(readyProbeDirectory, "StartServer64.bat"), ("""
        @echo off
        echo LOG  : General      f:0 st:775,941,047^> *** SERVER STARTED ****
        type nul >general.flag
        "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "$until=[DateTime]::UtcNow.AddSeconds(15); while (!(Test-Path -LiteralPath 'emit.flag') -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 30 }"
        echo LOG  : Network      f:0 st:775,941,047^> *** SERVER STARTED ****
        "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "$until=[DateTime]::UtcNow.AddSeconds(15); while (!(Test-Path -LiteralPath 'release.flag') -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 30 }"
        exit /b 0

        """ + forwardingScript).ReplaceLineEndings("\r\n"));
    Check(windowsSessions.Start(readyProbeDirectory, namedProfile).Started, "The Windows readiness fixture should start.");
    try
    {
        timeout.Restart();
        while (!File.Exists(generalFile) && timeout.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(25);
        Check(File.Exists(generalFile) && windowsSessions.GetStatus(namedProfile) == ServerSessionStatus.Starting,
            "An output line that only resembles the network readiness signal must not mark the session running.");
        File.WriteAllText(Path.Combine(readyProbeDirectory, "emit.flag"), "");
        timeout.Restart();
        while (windowsSessions.GetStatus(namedProfile) == ServerSessionStatus.Starting
            && timeout.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(25);
        Check(windowsSessions.GetStatus(namedProfile) == ServerSessionStatus.Running
            && windowsSessions.GetStatus(alphaProfile) == ServerSessionStatus.NotObserved,
            "The exact Network log signal must mark only the launcher that actually printed it as running.");
    }
    finally
    {
        File.WriteAllText(Path.Combine(readyProbeDirectory, "emit.flag"), "");
        File.WriteAllText(releaseFile, "");
        timeout.Restart();
        while (windowsSessions.GetStatus(namedProfile) is ServerSessionStatus.Starting or ServerSessionStatus.Running
            && timeout.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(25);
    }
    Console.WriteLine("PASS: exact Windows network readiness output belongs only to the launcher that printed it");

    var stopProbeDirectory = Directory.CreateDirectory(Path.Combine(sandbox.Path, "Stop probe & spaces")).FullName;
    string CommandPath(ServerProfile profile) => Path.Combine(stopProbeDirectory, $"{profile.Name}.command.txt");
    string ReleasePath(ServerProfile profile) => Path.Combine(stopProbeDirectory, $"{profile.Name}.exit.flag");
    File.WriteAllText(Path.Combine(stopProbeDirectory, "StartServer64.bat"), ("""
        @echo off
        set "PZ_TEST_PROFILE=%~2"
        "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File .\receive.ps1
        if errorlevel 1 exit /b 1
        pause
        exit /b 0

        """ + forwardingScript).ReplaceLineEndings("\r\n"));
    File.WriteAllText(Path.Combine(stopProbeDirectory, "receive.ps1"), """
        Write-Output "Startup prompt for ${env:PZ_TEST_PROFILE}"
        $stream = [IO.StreamReader]::new([Console]::OpenStandardInput())
        $setup = $stream.ReadLineAsync()
        if (!$setup.Wait(15000) -or $setup.Result -ne "setup ${env:PZ_TEST_PROFILE}") { exit 2 }
        Write-Output 'LOG  : Network      f:0 st:775,941,047> *** SERVER STARTED ****'
        $line = $stream.ReadLineAsync()
        if (!$line.Wait(25000)) { exit 1 }
        $received = Join-Path $PWD "$env:PZ_TEST_PROFILE.command.txt"
        [IO.File]::WriteAllText("$received.tmp", [string]$line.Result)
        [IO.File]::Move("$received.tmp", $received)
        $release = "$env:PZ_TEST_PROFILE.exit.flag"
        $until = [DateTime]::UtcNow.AddSeconds(10)
        while (!(Test-Path -LiteralPath $release) -and [DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 25 }
        """);
    var concurrentSessions = new ServerLaunches();
    Check(!concurrentSessions.RequestStop(alphaProfile).Accepted, "An unowned profile must not receive a stop request.");
    try
    {
        Check(concurrentSessions.Start(stopProbeDirectory, namedProfile).Started
            && concurrentSessions.Start(stopProbeDirectory, alphaProfile).Started,
            "Both profiles must launch from the same Windows installation.");
        await WaitUntil(() => concurrentSessions.ReadConsole(namedProfile).Contains($"Startup prompt for {namedProfile.Name}")
            && concurrentSessions.ReadConsole(alphaProfile).Contains($"Startup prompt for {alphaProfile.Name}"),
            "Both headless processes should show their own startup prompt.");
        Check(!concurrentSessions.ReadConsole(namedProfile).Contains($"Startup prompt for {alphaProfile.Name}"),
            "One profile must not show another profile's output.");
        var firstSend = await concurrentSessions.SendConsoleLineAsync(namedProfile, $"setup {namedProfile.Name}");
        var secondSend = await concurrentSessions.SendConsoleLineAsync(alphaProfile, $"setup {alphaProfile.Name}");
        Check(firstSend is null && secondSend is null, $"Headless input was not sent: first={firstSend}, second={secondSend}");
        await WaitUntil(() => concurrentSessions.GetStatus(namedProfile) == ServerSessionStatus.Running
            && concurrentSessions.GetStatus(alphaProfile) == ServerSessionStatus.Running,
            "Both Windows sessions must reach readiness at the same time.");
        Check(concurrentSessions.RequestStop(namedProfile).Accepted, "The selected Windows session should accept a stop.");
        await WaitUntil(() => File.Exists(CommandPath(namedProfile)), "Quit did not reach the selected process.");
        Check(File.ReadAllText(CommandPath(namedProfile)) == "quit"
            && concurrentSessions.GetStatus(namedProfile) == ServerSessionStatus.Stopping
            && concurrentSessions.GetStatus(alphaProfile) == ServerSessionStatus.Running
            && !File.Exists(CommandPath(alphaProfile)),
            "The exact quit command must reach only the selected child while both processes are still alive.");

        File.WriteAllText(ReleasePath(namedProfile), "");
        await WaitUntil(() => concurrentSessions.GetStatus(namedProfile) == ServerSessionStatus.Stopped,
            "The selected launcher should exit even with a trailing PAUSE.");
        Check(concurrentSessions.GetStatus(alphaProfile) == ServerSessionStatus.Running
            && !File.Exists(CommandPath(alphaProfile)), "The other session must still run and not have received a command.");

        concurrentSessions.RequestShutdownStops();
        Check(concurrentSessions.PendingShutdownProfiles().SequenceEqual(new[] { alphaProfile.Name }),
            "Shutdown must wait for the other session to exit.");
        await WaitUntil(() => File.Exists(CommandPath(alphaProfile)), "Quit did not reach the other process.");
        Check(File.ReadAllText(CommandPath(alphaProfile)) == "quit", "The other process received an unexpected command.");
        File.WriteAllText(ReleasePath(alphaProfile), "");
        await WaitUntil(() => concurrentSessions.GetStatus(alphaProfile) == ServerSessionStatus.Stopped,
            "The other launcher should exit after receiving its own quit.");
    }
    finally
    {
        foreach (var profile in new[] { namedProfile, alphaProfile })
        {
            File.WriteAllText(ReleasePath(profile), "");
            concurrentSessions.RequestStop(profile);
        }
        var cleanup = Stopwatch.StartNew();
        while (concurrentSessions.PendingShutdownProfiles().Count > 0
            && cleanup.Elapsed < TimeSpan.FromSeconds(18))
            await Task.Delay(100);
    }
    Console.WriteLine("PASS: two headless Windows sessions accept separate startup input and quit, even with PAUSE");
}

static async Task WaitUntil(Func<bool> condition, string message)
{
    var timeout = Stopwatch.StartNew();
    while (!condition())
    {
        Check(timeout.Elapsed < TimeSpan.FromSeconds(10), message);
        await Task.Delay(25);
    }
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class TestDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("pz-installation-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException error)
        {
            // A failing child fixture can retain a log handle briefly. Do not mask
            // the actual assertion failure as a directory cleanup error.
            Console.Error.WriteLine($"Could not clean the temporary fixture directory: {error.Message}");
        }
    }
}

sealed class FakeLaunchProcess : IServerLaunchProcess
{
    public bool HasExited { get; set; }
    public bool ExitConfirmed { get; set; } = true;
    public bool ReadinessConfirmed { get; set; }
    public int StopRequests { get; private set; }
    public string ConsoleText { get; set; } = "";
    public List<string> Received { get; } = [];
    public void RequestGracefulStop() => StopRequests++;
    public string ReadConsoleTail() => ConsoleText;
    public Task SendConsoleLineAsync(string line)
    {
        Received.Add(line);
        return Task.CompletedTask;
    }
    public void Dispose() { }
}

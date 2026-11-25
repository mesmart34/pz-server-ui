using PzServerUi.Application;

using var sandbox = new TestDirectory();
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

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class TestDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("pz-installation-test-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

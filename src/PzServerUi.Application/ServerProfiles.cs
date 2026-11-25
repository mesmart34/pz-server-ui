namespace PzServerUi.Application;

public sealed record ServerProfile(string Name, string ConfigurationPath, string? WorldPath);

public sealed class ServerProfiles(string dataDirectory)
{
    public string ConfigurationDirectory { get; } = Path.Combine(dataDirectory, "Server");
    public IReadOnlyList<ServerProfile> Items { get; private set; } = [];
    public ServerProfile? Selected { get; private set; }

    public void Refresh()
    {
        var selectedName = Selected?.Name;
        var worldsDirectory = Path.Combine(dataDirectory, "Saves", "Multiplayer");
        ServerProfile[] discovered;
        try
        {
            discovered = Directory.EnumerateFiles(ConfigurationDirectory)
                .Where(path => Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase))
                .Select(path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var world = Path.Combine(worldsDirectory, name);
                    return new ServerProfile(name, path, Directory.Exists(world) ? world : null);
                })
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            discovered = [];
        }

        Items = Array.AsReadOnly(discovered);
        Selected = Find(selectedName) ?? Items.FirstOrDefault();
    }

    public void Select(string name) => Selected = Find(name) ?? Selected;

    private ServerProfile? Find(string? name) =>
        Items.FirstOrDefault(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
}

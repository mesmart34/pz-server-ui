namespace PzServerUi.Application;

public sealed class ServerProfileMods
{
    private ServerProfileMods(string[] modIds, string[] workshopIds)
    {
        ModIds = Array.AsReadOnly(modIds);
        WorkshopIds = Array.AsReadOnly(workshopIds);
    }

    public IReadOnlyList<string> ModIds { get; }
    public IReadOnlyList<string> WorkshopIds { get; }

    public static ServerProfileMods Read(ServerProfile profile)
    {
        string[]? mods = null;
        string[]? workshop = null;
        foreach (var line in File.ReadLines(profile.ConfigurationPath))
        {
            var equals = line.IndexOf('=');
            if (equals < 0)
                continue;
            var key = line.AsSpan(0, equals).Trim();
            if (key.SequenceEqual("Mods"))
                mods = ReadList(mods, "Mods", line[(equals + 1)..]);
            else if (key.SequenceEqual("WorkshopItems"))
                workshop = ReadList(workshop, "WorkshopItems", line[(equals + 1)..]);
        }
        return new ServerProfileMods(mods ?? [], workshop ?? []);
    }

    private static string[] ReadList(string[]? previous, string key, string value)
    {
        if (previous is not null)
            throw new InvalidDataException($"Параметр {key} указан несколько раз.");
        // These are distinct lists, not pairs. Keep IDs opaque and in the order written.
        return value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}

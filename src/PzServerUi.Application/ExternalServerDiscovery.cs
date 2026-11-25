namespace PzServerUi.Application;

public enum ExternalServerStatus
{
    NotObserved,
    ProcessObserved,
    Ambiguous,
}

public sealed record DedicatedProcessCandidate(string ExecutablePath, IReadOnlyList<string> Arguments);

public static class ExternalServerDiscovery
{
    public static IReadOnlyDictionary<string, ExternalServerStatus> Match(
        string installationFolder, IEnumerable<DedicatedProcessCandidate> processes)
    {
        var executables = Executables(installationFolder);
        var statuses = new Dictionary<string, ExternalServerStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in processes)
        {
            if (!executables.Contains(process.ExecutablePath) || ProfileName(process.Arguments) is not { } name)
                continue;
            statuses[name] = statuses.ContainsKey(name)
                ? ExternalServerStatus.Ambiguous : ExternalServerStatus.ProcessObserved;
        }
        return statuses;
    }

    internal static HashSet<string> Executables(string installationFolder) =>
        (from jre in new[] { "jre", "jre32", "jre64" }
         from java in new[] { "java.exe", "javaw.exe" }
         select Path.GetFullPath(Path.Combine(installationFolder, jre, "bin", java)))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? ProfileName(IReadOnlyList<string> arguments)
    {
        var main = -1;
        for (var i = 1; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.StartsWith("-Duser.home", StringComparison.Ordinal)
                || argument.StartsWith('@') || argument is "-jar" or "-m" or "--module"
                || argument.StartsWith("--module=", StringComparison.Ordinal))
                return null;
            if (argument is "-cp" or "-classpath" or "--class-path" or "-p" or "--module-path"
                or "--upgrade-module-path" or "--add-modules" or "--limit-modules" or "--add-exports"
                or "--add-opens" or "--add-reads" or "--patch-module" or "--enable-native-access")
            {
                i++; // JVM option value; it cannot be the server's main class.
                continue;
            }
            if (argument.StartsWith('-'))
                continue;
            if (argument != "zombie.network.GameServer")
                return null;
            main = i;
            break;
        }
        if (main < 0)
            return null;

        string? name = null;
        for (var i = main + 1; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            // These can change which profile data belongs to the same name.
            if (argument.Equals("-cachedir", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("-cachedir=", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("-servername=", StringComparison.OrdinalIgnoreCase))
                return null;
            if (argument != "-servername")
                continue;
            if (name is not null || ++i >= arguments.Count || string.IsNullOrWhiteSpace(arguments[i])
                || arguments[i].StartsWith('-'))
                return null;
            name = arguments[i];
        }
        return name;
    }
}

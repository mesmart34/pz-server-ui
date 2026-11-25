using System.Globalization;
using System.Net.NetworkInformation;

namespace PzServerUi.Application;

internal readonly record struct ServerGamePorts(int DefaultPort, int UdpPort)
{
    public static ServerGamePorts Read(string configurationPath)
    {
        int? defaultPort = null;
        int? directPort = null;
        foreach (var line in File.ReadLines(configurationPath))
        {
            var equals = line.IndexOf('=');
            if (equals < 0)
                continue;
            var key = line[..equals].Trim();
            if (key == "DefaultPort")
                defaultPort = ReadSinglePort(defaultPort, key, line.AsSpan(equals + 1).Trim());
            else if (key == "UDPPort")
                directPort = ReadSinglePort(directPort, key, line.AsSpan(equals + 1).Trim());
            else if (key.Equals("DefaultPort", StringComparison.OrdinalIgnoreCase)
                || key.Equals("UDPPort", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Проверь регистр имён параметров DefaultPort и UDPPort в файле настроек.");
        }

        // Defaults from the Dedicated Server's standard ServerOptions.
        var ports = new ServerGamePorts(defaultPort ?? 16261, directPort ?? 16262);
        if (ports.DefaultPort == ports.UdpPort)
            throw new IOException($"DefaultPort и UDPPort используют один и тот же UDP {ports.DefaultPort}. Укажи разные порты.");
        return ports;
    }

    public bool Contains(int port) => DefaultPort == port || UdpPort == port;

    public static HashSet<int> ReadOccupiedUdpPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
                .Select(endpoint => endpoint.Port).ToHashSet();
        }
        catch (NetworkInformationException error)
        {
            throw new IOException("Не удалось проверить занятые UDP-порты на этом компьютере.", error);
        }
    }

    private static int ReadSinglePort(int? previous, string key, ReadOnlySpan<char> value)
    {
        if (previous is not null)
            throw new IOException($"Параметр {key} указан несколько раз. Нельзя надёжно определить порт.");
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
            throw new IOException($"Не удалось проверить {key}: укажи фиксированный порт от 1 до 65535.");
        return port;
    }
}

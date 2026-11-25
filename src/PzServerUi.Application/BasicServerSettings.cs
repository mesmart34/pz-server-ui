using System.Globalization;
using System.Text;

namespace PzServerUi.Application;

public sealed class BasicServerSettings
{
    private const string PlayersKey = "MaxPlayers";
    private const string PvpKey = "PVP";
    private const string DefaultPortKey = "DefaultPort";
    private const string UdpPortKey = "UDPPort";
    private static readonly string[] Keys = [PlayersKey, PvpKey, DefaultPortKey, UdpPortKey];
    private readonly ProfileConfigurationFile _file;
    private Dictionary<string, Field> _fields;

    private BasicServerSettings(ProfileConfigurationFile file)
    {
        _file = file;
        _fields = ReadFields(file.Text);
        _ = Pvp;
    }

    public string MaxPlayers => Get(PlayersKey, "32");
    public bool Pvp => bool.TryParse(Get(PvpKey, "true"), out var pvp)
        ? pvp : throw new InvalidDataException("Параметр PVP должен быть true или false.");
    public string DefaultPort => Get(DefaultPortKey, ServerGamePorts.StandardDefaultPort.ToString(CultureInfo.InvariantCulture));
    public string UdpPort => Get(UdpPortKey, ServerGamePorts.StandardUdpPort.ToString(CultureInfo.InvariantCulture));

    public static BasicServerSettings Open(ServerProfile profile) => new(ProfileConfigurationFile.Open(profile.ConfigurationPath));

    public bool Save(int maxPlayers, bool pvp, int defaultPort, int udpPort)
    {
        if (maxPlayers is < 1 or > 100)
            throw new ArgumentException("Лимит игроков должен быть от 1 до 100.");
        if (defaultPort is < 1 or > 65535 || udpPort is < 1 or > 65535)
            throw new ArgumentException("Игровые порты должны быть от 1 до 65535.");
        if (defaultPort == udpPort)
            throw new ArgumentException("Укажи разные значения для двух игровых портов.");

        var changes = new Dictionary<string, string>();
        AddNumber(PlayersKey, MaxPlayers, maxPlayers);
        if (pvp != Pvp)
            changes.Add(PvpKey, pvp ? "true" : "false");
        AddNumber(DefaultPortKey, DefaultPort, defaultPort);
        AddNumber(UdpPortKey, UdpPort, udpPort);
        if (changes.Count == 0)
            return _file.Save(_file.Text);

        var output = Rewrite(changes);
        var fields = ReadFields(output);
        if (!_file.Save(output))
            return false;
        _fields = fields;
        return true;

        void AddNumber(string key, string original, int desired)
        {
            if (!int.TryParse(original, NumberStyles.None, CultureInfo.InvariantCulture, out var current) || current != desired)
                changes.Add(key, desired.ToString(CultureInfo.InvariantCulture));
        }
    }

    private string Get(string key, string fallback) =>
        _fields.TryGetValue(key, out var field) ? _file.Text.Substring(field.Start, field.Length) : fallback;

    private static Dictionary<string, Field> ReadFields(string text)
    {
        var fields = new Dictionary<string, Field>(StringComparer.Ordinal);
        var index = text.StartsWith('\uFEFF') ? 1 : 0;
        while (index < text.Length)
        {
            var rest = text.AsSpan(index);
            var count = rest.IndexOfAny('\r', '\n');
            var line = count < 0 ? rest : rest[..count];
            var equals = line.IndexOf('=');
            if (equals >= 0)
            {
                var key = line[..equals].Trim();
                foreach (var known in Keys)
                {
                    if (!key.Equals(known, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!key.SequenceEqual(known))
                        throw new InvalidDataException($"Проверь регистр имени параметра {known}.");
                    var raw = line[(equals + 1)..];
                    var startTrimmed = raw.TrimStart();
                    var value = startTrimmed.TrimEnd();
                    if (!fields.TryAdd(known, new(index + equals + 1 + raw.Length - startTrimmed.Length, value.Length)))
                        throw new InvalidDataException($"Параметр {known} указан несколько раз.");
                    break;
                }
            }
            if (count < 0)
                break;
            index += count + (rest[count] == '\r' && count + 1 < rest.Length && rest[count + 1] == '\n' ? 2 : 1);
        }
        return fields;
    }

    private string Rewrite(Dictionary<string, string> changes)
    {
        var source = _file.Text;
        var output = new StringBuilder(source);
        foreach (var pair in changes.Where(pair => _fields.ContainsKey(pair.Key))
                     .OrderByDescending(pair => _fields[pair.Key].Start))
        {
            var field = _fields[pair.Key];
            output.Remove(field.Start, field.Length).Insert(field.Start, pair.Value);
        }

        var missing = changes.Where(pair => !_fields.ContainsKey(pair.Key)).ToArray();
        if (missing.Length == 0)
            return output.ToString();
        var firstBreak = source.AsSpan().IndexOfAny('\r', '\n');
        var newline = firstBreak < 0 ? Environment.NewLine : source[firstBreak] == '\n' ? "\n"
            : firstBreak + 1 < source.Length && source[firstBreak + 1] == '\n' ? "\r\n" : "\r";
        var endsWithNewline = source.EndsWith('\r') || source.EndsWith('\n');
        var hasContent = source.Length > (source.StartsWith('\uFEFF') ? 1 : 0);
        if (hasContent && !endsWithNewline)
            output.Append(newline);
        output.AppendJoin(newline, missing.Select(pair => $"{pair.Key}={pair.Value}"));
        if (endsWithNewline || !hasContent)
            output.Append(newline);
        return output.ToString();
    }

    private readonly record struct Field(int Start, int Length);
}

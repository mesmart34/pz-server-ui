using System.Globalization;
using System.Text;

namespace PzServerUi.Application;

public enum ServerIniGroup { General, Access, Network, Gameplay, Safehouses, Chat, Mods, Maintenance, Other }
public enum ServerIniKind { Text, Flag, Integer, Real, Secret }

public sealed class ServerIniField
{
    internal ServerIniField(string key, ServerIniDefinition definition, string value, bool ambiguous)
        => (Key, Definition, Value, IsAmbiguous) = (key, definition, value, ambiguous);

    public string Key { get; }
    public string Label => Definition.Label ?? Key;
    public ServerIniGroup Group => Definition.Group;
    public ServerIniKind Kind => Definition.Kind;
    public string Value { get; }
    public bool IsAmbiguous { get; }
    internal ServerIniDefinition Definition { get; }
}

// Edits a flat, game-generated INI without serializing the rest of the document.
public sealed class ServerIniSettings
{
    private readonly ProfileConfigurationFile _file;
    private Dictionary<string, List<ValueSpan>> _positions = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ServerIniField> _fields = new(StringComparer.Ordinal);

    private ServerIniSettings(ProfileConfigurationFile file)
    {
        _file = file;
        ReadFields();
    }

    public IReadOnlyList<ServerIniField> Fields { get; private set; } = [];

    public static ServerIniSettings Open(ServerProfile profile) => new(ProfileConfigurationFile.Open(profile.ConfigurationPath));

    public static string? ValidateModIds(string modId, string workshopId)
    {
        if (string.IsNullOrWhiteSpace(modId) || modId.Any(c => c == ';' || char.IsControl(c)))
            return "Укажи один Mod ID без точки с запятой и управляющих символов.";
        if (workshopId.Any(char.IsControl) || (workshopId.Trim() is { Length: > 0 } item
            && (!ulong.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)))
            return "Workshop ID должен быть положительным целым числом. Для локального мода оставь поле пустым.";
        return null;
    }

    public bool AddMod(string modId, string workshopId)
    {
        if (ValidateModIds(modId, workshopId) is { } error)
            throw new ArgumentException(error);

        var changes = new Dictionary<string, string>();
        AppendId("Mods", modId.Trim(), id => id);
        if (workshopId.Trim() is { Length: > 0 } item)
            AppendId("WorkshopItems", item, id => id.TrimStart('0'));
        return Save(changes);

        void AppendId(string key, string id, Func<string, string> identity)
        {
            var field = _fields[key];
            if (field.IsAmbiguous)
                throw new ArgumentException($"Нельзя однозначно изменить параметр {key}.");
            var value = field.Value;
            if (ServerProfileMods.ParseIds(value).Any(existing => identity(existing) == identity(id)))
                return;
            changes.Add(key, value.Length == 0 || value.EndsWith(';') ? value + id : value + ";" + id);
        }
    }

    public bool Save(IReadOnlyDictionary<string, string> requested)
    {
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in requested)
        {
            if (!_fields.TryGetValue(key, out var field) || field.IsAmbiguous)
                throw new ArgumentException($"Нельзя однозначно изменить параметр {key}.");
            if (field.Value == value)
                continue;
            if (value.Any(char.IsControl))
                throw new ArgumentException($"{field.Label}: введи значение в одну строку без управляющих символов.");
            var trimmed = value.Trim();
            if (field.Value == trimmed)
                continue;
            var definition = field.Definition;
            changes.Add(key, definition.Kind switch
            {
                ServerIniKind.Flag => bool.TryParse(trimmed, out var boolean)
                    ? (boolean ? "true" : "false")
                    : throw new ArgumentException($"{field.Label}: допустимы true и false."),
                ServerIniKind.Integer => ReadInteger(field, trimmed).ToString(CultureInfo.InvariantCulture),
                ServerIniKind.Real => double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    && double.IsFinite(number) ? trimmed
                    : throw new ArgumentException($"{field.Label}: введи число, используя точку для дробной части."),
                _ => trimmed
            });
        }

        foreach (var key in changes.Keys.Where(key => changes[key] == _fields[key].Value).ToArray())
            changes.Remove(key);
        ValidatePorts(changes);
        if (changes.Count == 0)
            return _file.Save(_file.Text);

        var output = new StringBuilder(_file.Text);
        foreach (var (span, value) in changes.Where(pair => _positions.ContainsKey(pair.Key))
                     .Select(pair => (_positions[pair.Key][0], pair.Value)).OrderByDescending(pair => pair.Item1.Start))
            output.Remove(span.Start, span.Length).Insert(span.Start, value);

        var additions = changes.Where(pair => !_positions.ContainsKey(pair.Key)).ToArray();
        if (additions.Length > 0)
            AppendMissing(output, additions);
        if (!_file.Save(output.ToString()))
            return false;
        ReadFields();
        return true;
    }

    private void ValidatePorts(Dictionary<string, string> changes)
    {
        const string primary = "DefaultPort", secondary = "UDPPort";
        if (!changes.ContainsKey(primary) && !changes.ContainsKey(secondary))
            return;
        var first = _fields[primary];
        var second = _fields[secondary];
        if (first.IsAmbiguous || second.IsAmbiguous)
            throw new ArgumentException("Устрани дубликат или неправильный регистр имени игрового порта в INI.");
        if (ReadInteger(first, changes.GetValueOrDefault(primary, first.Value))
            == ReadInteger(second, changes.GetValueOrDefault(secondary, second.Value)))
            throw new ArgumentException("Укажи разные значения для двух игровых портов.");
    }

    private static int ReadInteger(ServerIniField field, string value)
    {
        var definition = field.Definition;
        if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            && number >= definition.Min && number <= definition.Max)
            return number;
        throw new ArgumentException(definition.Min == int.MinValue && definition.Max == int.MaxValue
            ? $"{field.Label}: введи целое число."
            : $"{field.Label}: введи целое число от {definition.Min} до {definition.Max}.");
    }

    private void ReadFields()
    {
        _positions.Clear();
        _fields.Clear();
        var text = _file.Text;
        for (var index = text.StartsWith('\uFEFF') ? 1 : 0; index < text.Length;)
        {
            var rest = text.AsSpan(index);
            var end = rest.IndexOfAny('\r', '\n');
            var line = end < 0 ? rest : rest[..end];
            var equals = line.IndexOf('=');
            if (equals > 0)
            {
                var key = line[..equals].Trim();
                if (!key.IsEmpty && key[0] is not ('#' or ';' or '['))
                {
                    var name = key.ToString();
                    var raw = line[(equals + 1)..];
                    var value = raw.TrimStart();
                    var span = new ValueSpan(name, index + equals + 1 + raw.Length - value.Length, value.TrimEnd().Length);
                    if (!_positions.TryGetValue(name, out var entries))
                        _positions.Add(name, entries = []);
                    entries.Add(span);
                }
            }
            if (end < 0)
                break;
            index += end + (rest[end] == '\r' && end + 1 < rest.Length && rest[end + 1] == '\n' ? 2 : 1);
        }

        foreach (var spans in _positions.Values)
        {
            var first = spans[0];
            var key = ServerIniCatalog.CanonicalKey(first.Key);
            var ambiguous = spans.Count != 1 || key != first.Key;
            Add(key, ambiguous ? "" : text.Substring(first.Start, first.Length), ambiguous);
        }
        foreach (var (key, value) in ServerIniCatalog.Fallbacks)
            if (!_positions.ContainsKey(key))
                Add(key, value, false);
        Fields = Array.AsReadOnly(_fields.Values.OrderBy(field => field.Group)
            .ThenBy(field => field.Key, StringComparer.OrdinalIgnoreCase).ToArray());

        void Add(string key, string value, bool ambiguous) =>
            _fields.Add(key, new(key, ServerIniCatalog.Describe(key), value, ambiguous));
    }

    private void AppendMissing(StringBuilder output, KeyValuePair<string, string>[] additions)
    {
        var text = _file.Text;
        var end = text.AsSpan().IndexOfAny('\r', '\n');
        var newline = end < 0 ? Environment.NewLine : text[end] == '\n' ? "\n"
            : end + 1 < text.Length && text[end + 1] == '\n' ? "\r\n" : "\r";
        var hasEnding = text.EndsWith('\r') || text.EndsWith('\n');
        var hasContent = text.Length > (text.StartsWith('\uFEFF') ? 1 : 0);
        if (hasContent && !hasEnding)
            output.Append(newline);
        output.AppendJoin(newline, additions.Select(pair => $"{pair.Key}={pair.Value}"));
        if (hasEnding || !hasContent)
            output.Append(newline);
    }

    private readonly record struct ValueSpan(string Key, int Start, int Length);
}

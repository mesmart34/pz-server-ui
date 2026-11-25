using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PzServerUi.Application;

public sealed record SandboxDayLength(int Code, int Minutes);

public sealed class SandboxSettings
{
    private const string StarterKey = "StarterKit";
    private const string PointsKey = "CharacterFreePoints";
    private const string DayKey = "DayLength";
    private const string VersionKey = "VERSION";
    private readonly ProfileConfigurationFile _file;
    private LiteralTable _table;

    private SandboxSettings(ProfileConfigurationFile file)
    {
        _file = file;
        _table = Parse(file.Text);
        _ = StarterKit;
        _ = CharacterFreePoints;
        _ = DayLength;
        var version = _table.Contains(VersionKey) ? _table.Integer(VersionKey) : 0;
        var lengths = new List<SandboxDayLength> { new(1, 15), new(2, 30), new(3, 60) };
        // Sandbox format 4 (B41) and 6 (B42) differ from code 4 onwards.
        // For an unknown format, offer only codes with a shared meaning.
        if (version is 4 or 6)
        {
            if (version == 6)
                lengths.Add(new(4, 90));
            for (var hour = 2; hour <= 24; hour++)
                lengths.Add(new(lengths.Count + 1, hour * 60));
        }
        DayLengths = lengths.AsReadOnly();
    }

    public bool StarterKit => _table.Value(StarterKey) switch
    {
        "true" => true,
        "false" => false,
        _ => throw BadFile(),
    };
    public int CharacterFreePoints => _table.Integer(PointsKey);
    public int DayLength => _table.Integer(DayKey);
    public IReadOnlyList<SandboxDayLength> DayLengths { get; }

    public static SandboxSettings Open(ServerProfile profile)
    {
        var path = Path.Combine(Path.GetDirectoryName(profile.ConfigurationPath)!,
            Path.GetFileNameWithoutExtension(profile.ConfigurationPath) + "_SandboxVars.lua");
        return new(ProfileConfigurationFile.Open(path));
    }

    // A snapshot is kept by the form. Never rebase the form's values over edits made outside it.
    public bool Save(bool starterKit, int characterFreePoints, int dayLength)
    {
        if (characterFreePoints != CharacterFreePoints && characterFreePoints is < -100 or > 100)
            throw new ArgumentOutOfRangeException(nameof(characterFreePoints));
        if (dayLength != DayLength && !DayLengths.Any(option => option.Code == dayLength))
            throw new ArgumentOutOfRangeException(nameof(dayLength));

        var changes = new Dictionary<string, string>();
        if (starterKit != StarterKit)
            changes[StarterKey] = starterKit ? "true" : "false";
        if (characterFreePoints != CharacterFreePoints)
            changes[PointsKey] = characterFreePoints.ToString(CultureInfo.InvariantCulture);
        if (dayLength != DayLength)
            changes[DayKey] = dayLength.ToString(CultureInfo.InvariantCulture);
        if (changes.Count == 0)
            return _file.Save(_file.Text);
        var text = _table.Rewrite(changes);
        var updated = Parse(text);
        if (!_file.Save(text))
            return false;
        _table = updated;
        return true;
    }

    private static LiteralTable Parse(string text)
    {
        try { return new LiteralTable(text); }
        catch (RegexMatchTimeoutException) { throw BadFile(); }
    }

    private static InvalidDataException BadFile() => new(
        "Не удалось безопасно разобрать SandboxVars.lua. Нужна таблица SandboxVars с однозначными значениями настроек.");

    // Accept data literals, not executable Lua. Keep token offsets so other settings, comments,
    // line endings and an optional UTF-8 BOM pass through unchanged.
    private sealed class LiteralTable
    {
        private static readonly Regex Tokens = new(
            """
            \G(?:(?<blank>[\t \r\n\f\v]+|\A\uFEFF)|(?<long>(?<comment>--)?\[(?<equals>=*)\[[\s\S]*?\]\k<equals>\])|(?<comment>--(?!\[=*\[)[^\r\n]*)|(?<string>"(?:\\[\s\S]|[^"\\\r\n])*"|'(?:\\[\s\S]|[^'\\\r\n])*')|(?<name>[A-Za-z_][A-Za-z_0-9]*)|(?<number>-?(?:0[xX][0-9a-fA-F]+|(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?))|[{},;=\[\]])
            """, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

        private readonly string _text;
        private readonly Dictionary<string, Match> _fields = new(StringComparer.Ordinal);
        private Match? _next;
        private int _offset;

        public LiteralTable(string text)
        {
            _text = text;
            Advance();
            Expect("SandboxVars");
            Expect("=");
            Table(0);
            Take(";");
            if (_next is not null)
                throw BadFile();
        }

        public bool Contains(string key) => _fields.ContainsKey(key);
        public string Value(string key) => _fields.TryGetValue(key, out var value) ? value.Value : throw BadFile();
        public int Integer(string key) => int.TryParse(Value(key), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var value) ? value : throw BadFile();

        public string Rewrite(Dictionary<string, string> changes)
        {
            var builder = new StringBuilder(_text);
            foreach (var (token, value) in changes.Select(pair => (_fields[pair.Key], pair.Value))
                         .OrderByDescending(edit => edit.Item1.Index))
                builder.Remove(token.Index, token.Length).Insert(token.Index, value);
            return builder.ToString();
        }

        private void Table(int depth)
        {
            if (depth > 32)
                throw BadFile();
            Expect("{");
            while (!Take("}"))
            {
                var token = _next ?? throw BadFile();
                if (token.Groups["name"].Success)
                {
                    Advance();
                    if (Take("="))
                    {
                        var value = _next ?? throw BadFile();
                        ValueToken(depth);
                        if (depth == 0 && token.Value is StarterKey or PointsKey or DayKey or VersionKey
                            && !_fields.TryAdd(token.Value, value))
                            throw BadFile();
                    }
                    else if (depth == 0 || token.Value is not ("true" or "false" or "nil"))
                        throw BadFile();
                }
                else if (depth > 0 && Take("["))
                {
                    ValueToken(depth);
                    Expect("]");
                    Expect("=");
                    ValueToken(depth);
                }
                else if (depth > 0)
                    ValueToken(depth);
                else
                    throw BadFile();

                if (!Take(",") && !Take(";"))
                {
                    Expect("}");
                    return;
                }
            }
        }

        private void ValueToken(int depth)
        {
            var token = _next ?? throw BadFile();
            if (token.Value == "{")
                Table(depth + 1);
            else if (token.Groups["number"].Success || token.Groups["string"].Success
                     || token.Groups["long"].Success || token.Value is "true" or "false" or "nil")
                Advance();
            else
                throw BadFile();
        }

        private bool Take(string value)
        {
            if (_next?.Value != value)
                return false;
            Advance();
            return true;
        }

        private void Expect(string value)
        {
            if (!Take(value))
                throw BadFile();
        }

        private void Advance()
        {
            while (_offset < _text.Length)
            {
                var token = Tokens.Match(_text, _offset);
                if (!token.Success)
                    throw BadFile();
                _offset += token.Length;
                if (!token.Groups["blank"].Success && !token.Groups["comment"].Success)
                {
                    _next = token;
                    return;
                }
            }
            _next = null;
        }
    }
}

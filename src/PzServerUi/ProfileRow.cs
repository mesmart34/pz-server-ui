using System.ComponentModel;
using PzServerUi.Application;

namespace PzServerUi;

public sealed class ProfileRow(ServerProfile profile) : INotifyPropertyChanged
{
    private OverviewStatus? _status;

    public ServerProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public ImageSource? Cover { get; } = FindCover(profile.ConfigurationPath);
    public string Status => _status?.Short ?? "Проверка состояния";
    public Color StatusColor => GetColor(_status?.Tone ?? StatusTone.Neutral,
        Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Dark);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(OverviewStatus status)
    {
        // Notify the color when the system theme changes, even if the status does not.
        var beforeColor = _lastColor;
        var beforeText = Status;
        _status = status;
        _lastColor = StatusColor;
        if (beforeText != Status)
            PropertyChanged?.Invoke(this, new(nameof(Status)));
        if (!Equals(beforeColor, _lastColor))
            PropertyChanged?.Invoke(this, new(nameof(StatusColor)));
    }

    private Color? _lastColor;

    internal static Color GetColor(StatusTone tone, bool dark) => Color.FromArgb(tone switch
    {
        StatusTone.Ready => dark ? "#A8D598" : "#286029",
        StatusTone.Attention => dark ? "#E6C582" : "#795013",
        StatusTone.Error => dark ? "#FFA49C" : "#972C25",
        _ => dark ? "#BFC4B4" : "#535A4B"
    });

    private static ImageSource? FindCover(string configurationPath)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PzServerUi", "covers");
        var name = Path.GetFileNameWithoutExtension(configurationPath);
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg" })
        {
            var path = Path.Combine(directory, name + extension);
            if (File.Exists(path))
                return ImageSource.FromFile(path);
        }
        return null;
    }
}

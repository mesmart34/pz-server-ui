using PzServerUi.Application;

namespace PzServerUi;

public sealed class WorkshopCardTile(string id) : BindableObject
{
    private string? _previewPath;

    public string Id { get; } = id;
    public bool CanOpen => SteamWorkshopCatalog.IsWorkshopId(Id);
    public string Title { get; private set; } = id;
    public ImageSource? Cover { get; private set; }
    public bool HasCover => Cover is not null;

    public void Apply(WorkshopCard card)
    {
        if (Title != card.Title)
        {
            Title = card.Title;
            OnPropertyChanged(nameof(Title));
        }
        if (_previewPath != card.PreviewPath)
        {
            _previewPath = card.PreviewPath;
            Cover = _previewPath is { } path
                ? ImageSource.FromStream(() => File.OpenRead(path))
                : null;
            OnPropertyChanged(nameof(Cover));
            OnPropertyChanged(nameof(HasCover));
        }
    }
}

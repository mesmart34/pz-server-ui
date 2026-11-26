using PzServerUi.Application;

namespace PzServerUi;

public sealed class WorkshopCardTile(string id) : BindableObject
{
    private string? _previewPath;
    private string? _previewRevision;

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
        if (_previewPath != card.PreviewPath || _previewRevision != card.PreviewRevision)
        {
            _previewPath = card.PreviewPath;
            _previewRevision = card.PreviewRevision;
            Cover = _previewPath is { } path
                ? ImageSource.FromStream(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                : null;
            OnPropertyChanged(nameof(Cover));
            OnPropertyChanged(nameof(HasCover));
        }
    }
}

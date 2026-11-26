namespace PzServerUi;

public sealed class ResponsiveSettingsGrid : Grid
{
    private View? _anchor;
    private View? _details;
    private int _columns;

    public ResponsiveSettingsGrid()
    {
        ColumnSpacing = 12;
        RowSpacing = 10;
        Loaded += (_, _) => Reflow();
        SizeChanged += (_, _) =>
        {
            if (GetColumnCount() != _columns)
                Reflow();
        };
    }

    public double MinimumColumnWidth { get; set; } = 250;

    internal void SetDetails(View? anchor, View? details)
    {
        if (_details is not null)
            Children.Remove(_details);
        _anchor = anchor;
        _details = details;
        if (details is not null)
            Children.Add(details);
        Reflow();
    }

    private int GetColumnCount() => Width >= 3 * MinimumColumnWidth + 2 * ColumnSpacing ? 3
        : Width >= 2 * MinimumColumnWidth + ColumnSpacing ? 2 : 1;

    private void Reflow()
    {
        _columns = GetColumnCount();
        while (ColumnDefinitions.Count > _columns)
            ColumnDefinitions.RemoveAt(ColumnDefinitions.Count - 1);
        while (ColumnDefinitions.Count < _columns)
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });

        var itemCount = Children.Count - (_details is null ? 0 : 1);
        var rowCount = (itemCount + _columns - 1) / _columns + (_details is null ? 0 : 1);
        while (RowDefinitions.Count > rowCount)
            RowDefinitions.RemoveAt(RowDefinitions.Count - 1);
        while (RowDefinitions.Count < rowCount)
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var anchorIndex = _anchor is null ? -1 : Children.IndexOf(_anchor);
        var detailRow = anchorIndex < 0 ? rowCount - 1 : anchorIndex / _columns + 1;
        var index = 0;
        foreach (var child in Children)
        {
            if (child is not View view)
                continue;
            if (view == _details)
            {
                SetRow(view, detailRow);
                SetColumn(view, 0);
                SetColumnSpan(view, _columns);
                continue;
            }

            var row = index / _columns;
            SetRow(view, _details is not null && row >= detailRow ? row + 1 : row);
            SetColumn(view, index % _columns);
            index++;
        }
    }
}

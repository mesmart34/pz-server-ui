using System.Reflection;
using Microsoft.UI.Windowing;
using Color = Windows.UI.Color;

namespace PzServerUi;

public partial class ServerWindow : Window
{
    private static readonly string VersionLabel = typeof(ServerWindow).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { Length: > 0 } version
        ? $"v{version}"
        : "версия неизвестна";

    public ServerWindow()
    {
        InitializeComponent();
        Title = $"PZ Server UI {VersionLabel}";
        SetCompactTitleBar(false);
    }

    internal void SetCompactTitleBar(bool compact)
    {
        LocalModeIndicator.IsVisible = !compact;
        ServerTitleBar.Subtitle = compact ? VersionLabel : $"PROJECT ZOMBOID · {VersionLabel}";
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler?.PlatformView is not Microsoft.UI.Xaml.Window native
            || !AppWindowTitleBar.IsCustomizationSupported()
            || new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            return;

        var caption = native.AppWindow.TitleBar;
        // Keep the OS caption buttons and close event. Only blend their resting colors into the app chrome.
        caption.ButtonBackgroundColor = caption.ButtonInactiveBackgroundColor = Color.FromArgb(255, 25, 27, 22);
        caption.ButtonForegroundColor = Color.FromArgb(255, 239, 238, 229);
        caption.ButtonInactiveForegroundColor = Color.FromArgb(255, 173, 177, 161);
    }
}

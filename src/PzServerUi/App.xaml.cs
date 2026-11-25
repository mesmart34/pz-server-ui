using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace PzServerUi;

public partial class App : Microsoft.Maui.Controls.Application
{
    public App()
    {
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState) => new ServerWindow();
}

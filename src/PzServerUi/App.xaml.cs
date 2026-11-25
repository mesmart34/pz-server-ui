using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace PzServerUi;

public partial class App : Microsoft.Maui.Controls.Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new InstallationPage()) { Title = "PZ Server UI" };
}

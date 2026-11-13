using FortniteHelper.Services;
using Velopack;

namespace FortniteHelper;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: handles install / update / uninstall hooks from the Velopack installer, then returns.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Autostart.Apply(false))
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

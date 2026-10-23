using Velopack;
using Yrk.PdfStudio.App.Services;

namespace Yrk.PdfStudio.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: when the installer or updater starts the app with a hook argument, Velopack
        // handles it (registering "Open with", for example) and exits before any window appears.
        VelopackApp.Build()
            // Updates are installed only when the user clicks "Restart to update", after the signature check.
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => FileAssociation.Register())
            .OnAfterUpdateFastCallback(_ => FileAssociation.Register())
            .OnBeforeUninstallFastCallback(_ => FileAssociation.Unregister())
            .Run();

        // Already running: give it the file to open in a tab, and stop here.
        if (!SingleInstance.TryBecomeFirst() && SingleInstance.SendToFirst(args))
        {
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

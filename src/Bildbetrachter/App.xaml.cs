using System.Runtime;
using System.Windows;

namespace Bildbetrachter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Waehrend des Blaetterns moeglichst keine blockierenden Gen2-Collections.
        if (!GCSettings.IsServerGC)
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Bildbetrachter", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        new MainWindow(e.Args.FirstOrDefault()).Show();
    }
}

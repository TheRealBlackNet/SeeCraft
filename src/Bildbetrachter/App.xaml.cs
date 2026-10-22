using System.IO.Pipes;
using System.Runtime;
using System.Text;
using System.Windows;

namespace Bildbetrachter;

public partial class App : Application
{
    // Eine Instanz pro Windows-Sitzung/Benutzer. Weitere Starts reichen ihren Pfad an die laufende Instanz
    // weiter (Named Pipe, nur für den eigenen Benutzer erreichbar) und beenden sich sofort wieder.
    const string MutexName = @"Local\Bildbetrachter.SingleInstance";
    static readonly string PipeName = "Bildbetrachter." + Environment.UserName;

    Mutex? _mutex;
    MainWindow? _main;
    readonly CancellationTokenSource _cts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? arg = e.Args.FirstOrDefault();

        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst && SendToRunningInstance(arg))
        {
            Shutdown();
            return;
        }
        // (Antwortet die andere Instanz nicht, starten wir ganz normal weiter.)

        // Während des Blätterns moeglichst keine blockierenden Gen2-Collections.
        if (!GCSettings.IsServerGC)
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Bildbetrachter", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        _main = new MainWindow(arg);
        _main.Show();

        if (isFirst) StartPipeServer();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();
        base.OnExit(e);
    }

    // ---------------------------------------------------------------- zweite Instanz -> erste Instanz

    static bool SendToRunningInstance(string? arg)
    {
        string text = "";
        try { if (!string.IsNullOrWhiteSpace(arg)) text = Path.GetFullPath(arg.Trim('"')); }
        catch { /* ungültiger Pfad -> nur Fenster in den Vordergrund holen */ }

        Native.AllowSetForegroundWindow(-1);   // die laufende Instanz darf sich in den Vordergrund holen

        // Die erste Instanz startet möglicherweise gerade erst: bis zu ca. 5 Sekunden lang versuchen
        for (int i = 0; i < 50; i++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(100);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                writer.WriteLine(text);
                return true;
            }
            catch (TimeoutException) { }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    // ---------------------------------------------------------------- erste Instanz: Pipe-Server

    void StartPipeServer()
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);

                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readCts.CancelAfter(3000);   // hängender Client darf den Server nicht blockieren
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string line = (await reader.ReadLineAsync(readCts.Token)) ?? "";
                    if (line.Length > 32767) line = "";

                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        if (_main != null) await _main.OpenExternalAsync(line);
                    });
                }
                catch (OperationCanceledException) { }
                catch { await Task.Delay(200); }
            }
        });
    }
}

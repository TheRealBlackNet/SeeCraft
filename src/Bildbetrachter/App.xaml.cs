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

    // 1) register handler FIRST – any exception during single-instance
    //    detection must not kill or silently bypass the app
    DispatcherUnhandledException += (_, a) =>
    {
        MessageBox.Show(a.Exception.ToString(), "Bildbetrachter",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        a.Handled = true;
    };

    try
    {
        _mutex = new Mutex(true, MutexName, out bool isFirst);

        // 2) if a mutex exists but the owning process has crashed,
        //    treat it as "we are first" instead of "forwarding to nobody"
        if (!isFirst)
        {
            try
            {
                // AbandonedMutexException proves the owner is gone
                if (!_mutex.WaitOne(0))
                {
                    // we are a genuine second instance
                    if (SendToRunningInstance(arg))
                    {
                        Shutdown();
                        return;
                    }
                    // owner exists but isn't reachable -> fall through and
                    // become a working instance WITH a pipe server
                }
            }
            catch (AbandonedMutexException)
            {
                isFirst = true;               // previous instance died
            }
        }

        if (!GCSettings.IsServerGC)
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

        _main = new MainWindow(arg);
        _main.Show();

        // 3) always start the pipe server – otherwise a rare startup
        //    problem makes every later launch open a new window
        StartPipeServer();
    }
    catch (Exception ex)
    {
        MessageBox.Show(ex.Message, "Bildbetrachter – Startfehler",
            MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown();
    }
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
		catch { }

		// 4) Protect the P/Invoke – it must never abort the forward
		try { Native.AllowSetForegroundWindow(-1); } catch { }

		// 5) Retry long enough for a cold-start first instance (~10 s)
		for (int i = 0; i < 100; i++)
		{
			try
			{
				using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
				client.Connect(100);
				using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
				writer.WriteLine(text);
				return true;
			}
			catch (UnauthorizedAccessException) { return false; }
			catch (TimeoutException) { Thread.Sleep(100); }   // <-- was missing
			catch (IOException)      { Thread.Sleep(100); }
			catch                    { Thread.Sleep(100); }
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

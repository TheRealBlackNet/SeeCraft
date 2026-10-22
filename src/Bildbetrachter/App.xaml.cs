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
		DispatcherUnhandledException += (_, a) =>
		{
			MessageBox.Show(a.Exception.ToString(), "Bildbetrachter",
				MessageBoxButton.OK, MessageBoxImage.Warning);
			a.Handled = true;
		};

		string? arg = e.Args.FirstOrDefault();

		try
		{
			_mutex = new Mutex(true, MutexName, out bool isFirst);

			if (!isFirst)
			{
				if (SendToRunningInstance(arg))
				{
					Shutdown();
					return;
				}
				// Optional: statt still ein zweites Fenster zu öffnen, Fehler anzeigen/loggen
				System.Diagnostics.Debug.WriteLine("Weiterleitung fehlgeschlagen");
			}

			base.OnStartup(e);

			if (!GCSettings.IsServerGC)
				GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

			StartPipeServer();                 // zuerst Server, dann Fenster
			_main = new MainWindow(arg);
			_main.Show();
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

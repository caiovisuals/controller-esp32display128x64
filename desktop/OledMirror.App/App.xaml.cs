using System.Windows;
using System.Windows.Threading;
using OledMirror.Core.Configuration;
using OledMirror.Core.Logging;

namespace OledMirror.App;

public partial class App : Application
{
    public Logger Log { get; } = new();
    public MemoryLogSink LogSink { get; } = new();
    public SettingsStore SettingsStore { get; private set; } = null!;

    private FileLogSink? _fileLog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SettingsStore = new SettingsStore(log: Log);
        AppSettings settings = SettingsStore.Load();

        Log.MinimumLevel = settings.LogLevel;
        Log.AddSink(LogSink);

        if (settings.LogToFile)
        {
            try
            {
                _fileLog = new FileLogSink(SettingsStore.DefaultLogPath());
                Log.AddSink(_fileLog);
            }
            catch (Exception ex)
            {
                // Sem permissao de escrita nao e' motivo para nao abrir
                Log.Warning("app", $"Log em arquivo indisponivel: {ex.Message}");
            }
        }

        // Ultima linha de defesa: uma excecao nao tratada vira log e um aviso, nunca um encerramento silencioso da aplicacao
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("app", $"Excecao nao tratada: {args.ExceptionObject}");

        Log.Info("app", "OledMirror iniciado.");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("app", "Erro nao tratado na interface", e.Exception);
        MessageBox.Show(
            $"Ocorreu um erro inesperado:\n\n{e.Exception.Message}\n\nA aplicacao continua funcionando.",
            "OledMirror", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("app", "Encerrando.");
        _fileLog?.Dispose();
        base.OnExit(e);
    }
}
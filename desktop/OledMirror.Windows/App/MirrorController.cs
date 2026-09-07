using OledMirror.Core.Capture;
using OledMirror.Core.Configuration;
using OledMirror.Core.Devices;
using OledMirror.Core.Imaging;
using OledMirror.Core.Logging;
using OledMirror.Core.Pipeline;
using OledMirror.Core.Protocol;
using OledMirror.Core.Simulation;
using OledMirror.Core.Transport;
using OledMirror.Windows.Capture;

namespace OledMirror.Windows.App;

/// <summary>
/// Amarra link, pipeline, captura e configuracao. A ViewModel fala so com esta
/// classe; ela e' quem sabe montar um transporte real ou simulado, abrir a fonte
/// certa e manter tudo consistente quando as configuracoes mudam.
///
/// Separada da ViewModel de proposito: aqui nao ha nada de interface, o que
/// permite testar a orquestracao sem UI.
/// </summary>
public sealed class MirrorController : IDisposable
{
    private const string Cat = "app";

    private readonly Logger _log;
    private readonly ICaptureSourceProvider _capture = new WindowsCaptureSourceProvider();

    private DeviceLink? _link;
    private MirrorPipeline? _pipeline;
    private SimulatedDevice? _simulatedDevice;
    private AppSettings _settings = new();

    public MirrorController(Logger log)
    {
        _log = log;
        RelayCommand.UnhandledError += ex => _log.Error(Cat, "Falha ao executar comando", ex);
    }

    public DeviceLink? Link => _link;
    public MirrorPipeline? Pipeline => _pipeline;
    public ICaptureSourceProvider CaptureProvider => _capture;
    public AppSettings Settings => _settings;
    public bool IsRunning => _pipeline?.IsRunning == true;

    public event Action<LinkState>? LinkStateChanged;
    public event Action<DeviceHello>? DeviceIdentified;
    public event Action? FrameReady;

    // ------------------------------------------------------------------ conexao

    /// <summary>(Re)cria o link com as configuracoes atuais e comeca a conectar.</summary>
    public void Connect(AppSettings settings)
    {
        _settings = settings;
        Disconnect();

        Func<ITransport> factory;

        if (settings.SimulateDevice)
        {
            _log.Info(Cat, "Modo simulado: nenhum hardware sera usado.");
            factory = () =>
            {
                (LoopbackTransport host, LoopbackTransport device) = LoopbackTransport.CreatePair();
                _simulatedDevice?.Dispose();
                _simulatedDevice = new SimulatedDevice(device, _log);
                _simulatedDevice.Start();
                return host;
            };
        }
        else
        {
            string port = ResolvePortName(settings);
            _log.Info(Cat, $"Conectando em {port} a {settings.BaudRate} baud.");

            var transportOptions = new SerialTransportOptions
            {
                PortName = port,
                BaudRate = settings.BaudRate,
            };
            factory = () => new SerialPortTransport(transportOptions);
        }

        _link = new DeviceLink(factory, _log, new DeviceLinkOptions
        {
            AutoReconnect = settings.AutoReconnect,
        });

        _link.StateChanged += state => LinkStateChanged?.Invoke(state);
        _link.DeviceIdentified += device =>
        {
            DeviceIdentified?.Invoke(device);
            ApplyDisplaySettings();
        };

        _pipeline = new MirrorPipeline(_link, _log) { Settings = settings.ToMirrorSettings() };
        _pipeline.FrameReady += () => FrameReady?.Invoke();

        _link.Start();
    }

    /// <summary>Escolhe a porta: a configurada, ou a que mais parece um ESP32.</summary>
    private string ResolvePortName(AppSettings settings)
    {
        if (!settings.AutoDetectPort && !string.IsNullOrWhiteSpace(settings.PortName))
            return settings.PortName!;

        IReadOnlyList<SerialPortDescriptor> ports = ScanPorts();

        SerialPortDescriptor? chosen =
            ports.FirstOrDefault(p => string.Equals(p.PortName, settings.PortName, StringComparison.OrdinalIgnoreCase))
            ?? ports.FirstOrDefault(p => p.LooksLikeEsp32)
            ?? ports.FirstOrDefault();

        if (chosen is null)
        {
            _log.Warning(Cat, "Nenhuma porta serial encontrada.");
            return settings.PortName ?? "COM1";
        }

        if (!string.Equals(chosen.PortName, settings.PortName, StringComparison.OrdinalIgnoreCase))
            _log.Info(Cat, $"Porta detectada: {chosen}");

        return chosen.PortName;
    }

    public IReadOnlyList<SerialPortDescriptor> ScanPorts()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return new Devices.WmiSerialPortScanner().Scan();
        }
        catch (Exception ex)
        {
            _log.Warning(Cat, $"Enumeracao por WMI falhou ({ex.Message}); usando a basica.");
        }
        return new BasicSerialPortScanner().Scan();
    }

    public void Disconnect()
    {
        StopMirroring();

        _pipeline?.Dispose();
        _pipeline = null;

        _link?.Dispose();
        _link = null;

        _simulatedDevice?.Dispose();
        _simulatedDevice = null;
    }

    public void ForceReconnect() => _link?.ForceReconnect();

    // ------------------------------------------------------------------ espelhamento

    public void StartMirroring(AppSettings settings)
    {
        _settings = settings;
        if (_pipeline is null) Connect(settings);
        if (_pipeline is null) return;

        _pipeline.Settings = settings.ToMirrorSettings();
        _pipeline.SetSource(OpenSource(settings));
        _pipeline.Start();
    }

    public void StopMirroring() => _pipeline?.Stop();

    /// <summary>Aplica mudancas de configuracao sem reiniciar o espelhamento.</summary>
    public void UpdateSettings(AppSettings settings, bool sourceChanged)
    {
        _settings = settings;
        if (_pipeline is null) return;

        _pipeline.Settings = settings.ToMirrorSettings();
        if (sourceChanged) _pipeline.SetSource(OpenSource(settings));
    }

    private ICaptureSource OpenSource(AppSettings settings)
    {
        try
        {
            return settings.SourceKind switch
            {
                CaptureSourceKind.Window when !string.IsNullOrEmpty(settings.SourceId)
                    => _capture.OpenWindow(settings.SourceId!),
                CaptureSourceKind.Region
                    => _capture.OpenRegion(settings.RegionX, settings.RegionY, settings.RegionWidth, settings.RegionHeight),
                CaptureSourceKind.Synthetic
                    => new MockDesktopSource(),
                _ => _capture.OpenMonitor(settings.SourceId ?? string.Empty),
            };
        }
        catch (Exception ex)
        {
            // Janela fechada ou monitor removido entre a escolha e o inicio:
            // cai para o monitor principal em vez de nao iniciar.
            _log.Warning(Cat, $"Nao foi possivel abrir a fonte escolhida ({ex.Message}); usando o monitor principal.");
            return _capture.OpenMonitor(string.Empty);
        }
    }

    // ------------------------------------------------------------------ painel

    /// <summary>Envia contraste e inversao para o dispositivo.</summary>
    public void ApplyDisplaySettings()
    {
        if (_link is null || _link.State != LinkState.Connected) return;

        _link.SendConfig(Payloads.BuildConfig(
            (ConfigKey.Contrast, new[] { (byte)Math.Clamp(_settings.DisplayContrast, 0, 255) }),
            (ConfigKey.Invert, new[] { _settings.DisplayInvert ? (byte)1 : (byte)0 })));
    }

    public void SendText(string text) => _link?.SendText(text);

    public void ClearDisplay() => _link?.SendClear();

    /// <summary>Envia um padrao de teste (fase 3 do roteiro de integracao).</summary>
    public void SendTestPattern(TestPattern pattern)
    {
        if (_link is null) return;

        using var source = new TestPatternSource(pattern, 512, 256) { FixedTimeSeconds = 1.0 };
        var processor = new FrameProcessor(_settings.ToImageOptions());
        var frame = new byte[DisplayGeometry.FrameBytes];

        source.TryCapture(out CapturedFrame captured);
        processor.Process(captured, frame);

        _link.SendStreamBegin();
        if (_link.TrySendFrame(CommandId.FrameRaw, frame))
            _log.Info(Cat, $"Padrao de teste enviado: {pattern}");
        else
            _log.Warning(Cat, "Nao foi possivel enviar o padrao de teste.");
    }

    public void Dispose() => Disconnect();
}
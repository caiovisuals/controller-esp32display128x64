using System.Collections.ObjectModel;
using System.Windows.Input;
using OledMirror.Core.Capture;
using OledMirror.Core.Configuration;
using OledMirror.Core.Devices;
using OledMirror.Core.Imaging;
using OledMirror.Core.Logging;
using OledMirror.Core.Pipeline;
using OledMirror.Core.Protocol;
using OledMirror.Windows.Capture;

namespace OledMirror.Windows.App;

/// <summary>
/// Estado da janela principal. Nao referencia WPF: usa apenas
/// INotifyPropertyChanged, ICommand e um SynchronizationContext para voltar a
/// thread de UI. Isso mantem toda a logica da aplicacao compilavel e testavel
/// fora do WPF - a janela e' so XAML com binding.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string Cat = "ui";

    private readonly Logger _log;
    private readonly MemoryLogSink _logSink;
    private readonly SettingsStore _store;
    private readonly MirrorController _controller;
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _statsTimer;

    private readonly byte[] _previewFrame = new byte[DisplayGeometry.FrameBytes];
    private readonly AppSettings _settings;
    private bool _loading = true;

    public MainViewModel(Logger log, MemoryLogSink logSink, SettingsStore store)
    {
        _log = log;
        _logSink = logSink;
        _store = store;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _controller = new MirrorController(log);

        _settings = store.Load();

        StartCommand = new RelayCommand(Start, () => !IsRunning);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        ReconnectCommand = new RelayCommand(Reconnect);
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        RefreshSourcesCommand = new RelayCommand(RefreshSources);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ClearLogCommand = new RelayCommand(() => { _logSink.Clear(); LogEntries.Clear(); });
        SendTestPatternCommand = new RelayCommand(() => _controller.SendTestPattern(TestPattern.Checkerboard));
        SendHelloCommand = new RelayCommand(() => _controller.SendText("HELLO WORLD"));
        ClearDisplayCommand = new RelayCommand(() => _controller.ClearDisplay());

        _controller.LinkStateChanged += OnLinkStateChanged;
        _controller.DeviceIdentified += OnDeviceIdentified;
        _controller.FrameReady += OnFrameReady;

        _logSink.EntryWritten += OnLogEntry;

        MonitorEnumerator.EnableDpiAwareness();
        RefreshPorts();
        RefreshSources();
        ApplySettingsToProperties();
        _loading = false;

        _controller.Connect(_settings);

        // Atualiza os numeros duas vezes por segundo: rapido o bastante para
        // parecer vivo, devagar o bastante para nao competir com o pipeline.
        _statsTimer = new System.Threading.Timer(_ => RefreshStatistics(), null, 500, 500);
    }

    // ------------------------------------------------------------------ comandos

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ReconnectCommand { get; }
    public ICommand RefreshPortsCommand { get; }
    public ICommand RefreshSourcesCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand SendTestPatternCommand { get; }
    public ICommand SendHelloCommand { get; }
    public ICommand ClearDisplayCommand { get; }

    // ------------------------------------------------------------------ listas

    public ObservableCollection<SerialPortDescriptor> Ports { get; } = new();
    public ObservableCollection<CaptureSourceDescriptor> Monitors { get; } = new();
    public ObservableCollection<CaptureSourceDescriptor> Windows { get; } = new();
    public ObservableCollection<string> LogEntries { get; } = new();

    public IReadOnlyList<CaptureSourceKind> SourceKinds { get; } =
        new[] { CaptureSourceKind.Monitor, CaptureSourceKind.Window, CaptureSourceKind.Region, CaptureSourceKind.Synthetic };

    public IReadOnlyList<ResizeMode> ResizeModes { get; } = Enum.GetValues<ResizeMode>();
    public IReadOnlyList<DitheringMode> DitheringModes { get; } = Enum.GetValues<DitheringMode>();
    public IReadOnlyList<FrameEncoding> Encodings { get; } = Enum.GetValues<FrameEncoding>();
    public IReadOnlyList<int> FpsOptions { get; } = MirrorSettings.SupportedFps;
    public IReadOnlyList<int> BaudRates { get; } = new[] { 115200, 230400, 460800, 921600, 1500000 };

    // ------------------------------------------------------------------ conexao

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; private set => SetField(ref _isConnected, value); }

    private string _connectionStatus = "Desconectado";
    public string ConnectionStatus { get => _connectionStatus; private set => SetField(ref _connectionStatus, value); }

    private string _deviceDescription = "-";
    public string DeviceDescription { get => _deviceDescription; private set => SetField(ref _deviceDescription, value); }

    private SerialPortDescriptor? _selectedPort;
    public SerialPortDescriptor? SelectedPort
    {
        get => _selectedPort;
        set
        {
            if (!SetField(ref _selectedPort, value) || _loading) return;
            _settings.PortName = value?.PortName;
            _settings.AutoDetectPort = value is null;
            Reconnect();
        }
    }

    public int BaudRate
    {
        get => _settings.BaudRate;
        set { if (_settings.BaudRate == value) return; _settings.BaudRate = value; OnPropertyChanged(); if (!_loading) Reconnect(); }
    }

    public bool SimulateDevice
    {
        get => _settings.SimulateDevice;
        set { if (_settings.SimulateDevice == value) return; _settings.SimulateDevice = value; OnPropertyChanged(); if (!_loading) Reconnect(); }
    }

    public bool AutoReconnect
    {
        get => _settings.AutoReconnect;
        set { if (_settings.AutoReconnect == value) return; _settings.AutoReconnect = value; OnPropertyChanged(); }
    }

    // ------------------------------------------------------------------ fonte

    public CaptureSourceKind SourceKind
    {
        get => _settings.SourceKind;
        set
        {
            if (_settings.SourceKind == value) return;
            _settings.SourceKind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMonitorSource));
            OnPropertyChanged(nameof(IsWindowSource));
            OnPropertyChanged(nameof(IsRegionSource));
            if (!_loading) _controller.UpdateSettings(_settings, sourceChanged: true);
        }
    }

    public bool IsMonitorSource => SourceKind == CaptureSourceKind.Monitor;
    public bool IsWindowSource => SourceKind == CaptureSourceKind.Window;
    public bool IsRegionSource => SourceKind == CaptureSourceKind.Region;

    private CaptureSourceDescriptor? _selectedMonitor;
    public CaptureSourceDescriptor? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (!SetField(ref _selectedMonitor, value) || _loading || value is null) return;
            _settings.SourceId = value.Id;
            _controller.UpdateSettings(_settings, sourceChanged: SourceKind == CaptureSourceKind.Monitor);
        }
    }

    private CaptureSourceDescriptor? _selectedWindow;
    public CaptureSourceDescriptor? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (!SetField(ref _selectedWindow, value) || _loading || value is null) return;
            _settings.SourceId = value.Id;
            _controller.UpdateSettings(_settings, sourceChanged: SourceKind == CaptureSourceKind.Window);
        }
    }

    public int RegionX { get => _settings.RegionX; set { _settings.RegionX = value; OnPropertyChanged(); RegionChanged(); } }
    public int RegionY { get => _settings.RegionY; set { _settings.RegionY = value; OnPropertyChanged(); RegionChanged(); } }
    public int RegionWidth { get => _settings.RegionWidth; set { _settings.RegionWidth = Math.Max(16, value); OnPropertyChanged(); RegionChanged(); } }
    public int RegionHeight { get => _settings.RegionHeight; set { _settings.RegionHeight = Math.Max(16, value); OnPropertyChanged(); RegionChanged(); } }

    private void RegionChanged()
    {
        if (_loading || SourceKind != CaptureSourceKind.Region) return;
        _controller.UpdateSettings(_settings, sourceChanged: true);
    }

    // ------------------------------------------------------------------ processamento

    public ResizeMode ResizeMode
    {
        get => _settings.ResizeMode;
        set { if (_settings.ResizeMode == value) return; _settings.ResizeMode = value; OnPropertyChanged(); PushSettings(); }
    }

    public DitheringMode Dithering
    {
        get => _settings.Dithering;
        set { if (_settings.Dithering == value) return; _settings.Dithering = value; OnPropertyChanged(); PushSettings(); }
    }

    public FrameEncoding Encoding
    {
        get => _settings.Encoding;
        set { if (_settings.Encoding == value) return; _settings.Encoding = value; OnPropertyChanged(); PushSettings(); }
    }

    public int TargetFps
    {
        get => _settings.TargetFps;
        set { if (_settings.TargetFps == value) return; _settings.TargetFps = value; OnPropertyChanged(); PushSettings(); }
    }

    public int Threshold
    {
        get => _settings.Threshold;
        set { if (_settings.Threshold == value) return; _settings.Threshold = value; OnPropertyChanged(); PushSettings(); }
    }

    public double Contrast
    {
        get => _settings.Contrast;
        set { if (Math.Abs(_settings.Contrast - value) < 0.001) return; _settings.Contrast = value; OnPropertyChanged(); PushSettings(); }
    }

    public double Gamma
    {
        get => _settings.Gamma;
        set { if (Math.Abs(_settings.Gamma - value) < 0.001) return; _settings.Gamma = value; OnPropertyChanged(); PushSettings(); }
    }

    public bool AutoContrast
    {
        get => _settings.AutoContrast;
        set { if (_settings.AutoContrast == value) return; _settings.AutoContrast = value; OnPropertyChanged(); PushSettings(); }
    }

    public bool Invert
    {
        get => _settings.Invert;
        set { if (_settings.Invert == value) return; _settings.Invert = value; OnPropertyChanged(); PushSettings(); }
    }

    public bool SkipUnchangedFrames
    {
        get => _settings.SkipUnchangedFrames;
        set { if (_settings.SkipUnchangedFrames == value) return; _settings.SkipUnchangedFrames = value; OnPropertyChanged(); PushSettings(); }
    }

    public bool PreviewOnly
    {
        get => _settings.PreviewOnly;
        set { if (_settings.PreviewOnly == value) return; _settings.PreviewOnly = value; OnPropertyChanged(); PushSettings(); }
    }

    public int DisplayContrast
    {
        get => _settings.DisplayContrast;
        set
        {
            if (_settings.DisplayContrast == value) return;
            _settings.DisplayContrast = value;
            OnPropertyChanged();
            if (!_loading) _controller.ApplyDisplaySettings();
        }
    }

    private void PushSettings()
    {
        if (_loading) return;
        _controller.UpdateSettings(_settings, sourceChanged: false);
    }

    // ------------------------------------------------------------------ metricas

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value)) return;
            (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private string _sourceResolution = "-";
    public string SourceResolution { get => _sourceResolution; private set => SetField(ref _sourceResolution, value); }

    public string OutputResolution => $"{DisplayGeometry.Width}x{DisplayGeometry.Height}";

    private string _effectiveFps = "0,0";
    public string EffectiveFps { get => _effectiveFps; private set => SetField(ref _effectiveFps, value); }

    private string _latency = "-";
    public string Latency { get => _latency; private set => SetField(ref _latency, value); }

    private string _bytesSent = "0";
    public string BytesSent { get => _bytesSent; private set => SetField(ref _bytesSent, value); }

    private string _framesSent = "0";
    public string FramesSent { get => _framesSent; private set => SetField(ref _framesSent, value); }

    private string _errors = "0";
    public string Errors { get => _errors; private set => SetField(ref _errors, value); }

    private string _bandwidth = "-";
    public string Bandwidth { get => _bandwidth; private set => SetField(ref _bandwidth, value); }

    private string _timings = "-";
    public string Timings { get => _timings; private set => SetField(ref _timings, value); }

    /// <summary>Ultimo frame empacotado (1024 bytes), para a previa desenhar.</summary>
    public byte[] PreviewFrame => _previewFrame;

    /// <summary>Disparado quando <see cref="PreviewFrame"/> tem conteudo novo.</summary>
    public event Action? PreviewUpdated;

    // ------------------------------------------------------------------ acoes

    private void Start()
    {
        _controller.StartMirroring(_settings);
        IsRunning = true;
    }

    private void Stop()
    {
        _controller.StopMirroring();
        IsRunning = false;
    }

    private void Reconnect()
    {
        bool wasRunning = IsRunning;
        Stop();
        _controller.Connect(_settings);
        if (wasRunning) Start();
    }

    private void RefreshPorts()
    {
        IReadOnlyList<SerialPortDescriptor> ports = _controller.ScanPorts();

        Ports.Clear();
        foreach (SerialPortDescriptor port in ports) Ports.Add(port);

        _selectedPort = ports.FirstOrDefault(p => string.Equals(p.PortName, _settings.PortName, StringComparison.OrdinalIgnoreCase))
                     ?? ports.FirstOrDefault(p => p.LooksLikeEsp32)
                     ?? ports.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedPort));

        if (ports.Count == 0) _log.Warning(Cat, "Nenhuma porta serial encontrada.");
    }

    private void RefreshSources()
    {
        Monitors.Clear();
        foreach (CaptureSourceDescriptor m in _controller.CaptureProvider.EnumerateMonitors()) Monitors.Add(m);

        Windows.Clear();
        foreach (CaptureSourceDescriptor w in _controller.CaptureProvider.EnumerateWindows()) Windows.Add(w);

        _selectedMonitor = Monitors.FirstOrDefault(m => m.Id == _settings.SourceId) ?? Monitors.FirstOrDefault();
        _selectedWindow = Windows.FirstOrDefault(w => w.Id == _settings.SourceId) ?? Windows.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedMonitor));
        OnPropertyChanged(nameof(SelectedWindow));
    }

    private void SaveSettings()
    {
        if (_store.Save(_settings)) _log.Info(Cat, $"Configuracao salva em {_store.Path}.");
    }

    private void ApplySettingsToProperties()
    {
        foreach (string name in new[]
        {
            nameof(BaudRate), nameof(SimulateDevice), nameof(AutoReconnect), nameof(SourceKind),
            nameof(IsMonitorSource), nameof(IsWindowSource), nameof(IsRegionSource),
            nameof(RegionX), nameof(RegionY), nameof(RegionWidth), nameof(RegionHeight),
            nameof(ResizeMode), nameof(Dithering), nameof(Encoding), nameof(TargetFps),
            nameof(Threshold), nameof(Contrast), nameof(Gamma), nameof(AutoContrast),
            nameof(Invert), nameof(SkipUnchangedFrames), nameof(PreviewOnly), nameof(DisplayContrast),
        })
        {
            OnPropertyChanged(name);
        }
    }

    // ------------------------------------------------------------------ eventos

    private void OnLinkStateChanged(LinkState state) => Post(() =>
    {
        IsConnected = state == LinkState.Connected;
        ConnectionStatus = state switch
        {
            LinkState.Connected => "ESP32 conectado",
            LinkState.Connecting => "Conectando...",
            LinkState.Handshaking => "Handshake...",
            LinkState.Reconnecting => "Reconectando...",
            LinkState.Faulted => "Falha de conexao",
            _ => "Desconectado",
        };
        if (!IsConnected) DeviceDescription = "-";
    });

    private void OnDeviceIdentified(DeviceHello device) => Post(() => DeviceDescription = device.ToString());

    private void OnFrameReady()
    {
        MirrorPipeline? pipeline = _controller.Pipeline;
        if (pipeline is null) return;

        // Copia fora da thread de UI; o evento so avisa que ha conteudo novo.
        lock (_previewFrame)
        {
            if (!pipeline.TryCopyLatestFrame(_previewFrame)) return;
        }
        Post(() => PreviewUpdated?.Invoke());
    }

    private void OnLogEntry(LogEntry entry) => Post(() =>
    {
        LogEntries.Add(entry.Format());
        // Mantem a lista curta: a UI nao precisa guardar a sessao inteira, o
        // arquivo de log guarda.
        while (LogEntries.Count > 500) LogEntries.RemoveAt(0);
    });

    private void RefreshStatistics()
    {
        MirrorPipeline? pipeline = _controller.Pipeline;
        DeviceLink? link = _controller.Link;
        if (pipeline is null) return;

        PipelineStatistics p = pipeline.Statistics.Snapshot();
        LinkStatistics? l = link?.Statistics.Snapshot();
        EncoderStatistics e = pipeline.EncoderStatistics.Snapshot();
        (int width, int height) = pipeline.SourceResolution;

        Post(() =>
        {
            IsRunning = pipeline.IsRunning;
            SourceResolution = width > 0 ? $"{width}x{height}" : "-";
            EffectiveFps = $"{p.EffectiveFps:F1}";
            Latency = l is null || l.AverageLatencyMs <= 0 ? "-" : $"{l.AverageLatencyMs:F0} ms";
            BytesSent = FormatBytes(l?.BytesSent ?? 0);
            FramesSent = $"{p.FramesSent}";
            Errors = l is null
                ? "0"
                : $"{l.Nacks + l.FrameAckTimeouts + p.CaptureErrors + l.DeviceErrors}";
            Bandwidth = e.RawEquivalentBytes == 0
                ? "-"
                : $"{p.LastPayloadBytes} B/frame ({e.CompressionRatio:P0} do cru)";
            Timings = $"cap {p.CaptureMs:F1} | proc {p.ProcessMs:F1} | env {p.SendMs:F1} ms";
        });
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
    };

    private void Post(Action action) => _ui.Post(_ => action(), null);

    public void Dispose()
    {
        _statsTimer.Dispose();
        _logSink.EntryWritten -= OnLogEntry;
        SaveSettings();
        _controller.Dispose();
    }
}
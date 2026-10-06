using OledMirror.Core.Imaging;
using OledMirror.Core.Protocol;

namespace OledMirror.Core.Pipeline;

/// <summary>Configuracao do espelhamento. Trocar o objeto inteiro com o pipeline rodando e' seguro.</summary>
public sealed class MirrorSettings
{
    public static IReadOnlyList<int> SupportedFps { get; } = new[] { 5, 10, 15, 20, 25, 30 };

    public int TargetFps { get; set; } = 10;
    public FrameEncoding Encoding { get; set; } = FrameEncoding.Auto;

    /// <summary>Frame identico ao ultimo enviado nao vai para o fio.</summary>
    public bool SkipUnchangedFrames { get; set; } = true;

    /// <summary>
    /// Com a tela parada, reenvia um frame completo a cada este tempo (0 desliga).
    /// Custa poucos bytes por segundo e faz o painel se recuperar sozinho se o
    /// ESP32 reiniciar com a aplicacao conectada - sem isso ele ficaria na tela de
    /// espera ate a tela do PC mudar.
    /// </summary>
    public int KeyframeIntervalMs { get; set; } = 3000;

    /// <summary>Processa e mostra a previa, mas nao envia nada ao dispositivo.</summary>
    public bool PreviewOnly { get; set; }

    public ImageProcessorOptions Image { get; set; } = new();
}

/// <summary>Metricas do pipeline. Atualizadas pela thread do pipeline; leia com <see cref="Snapshot"/>.</summary>
public sealed class PipelineStatistics
{
    internal long _captured, _processed, _sent, _skipped, _captureErrors, _lastPayloadBytes;
    private readonly object _gate = new();
    private double _effectiveFps, _captureMs, _processMs, _encodeMs, _sendMs, _totalMs;
    private int _requestedFps;

    public int RequestedFps { get { lock (_gate) return _requestedFps; } internal set { lock (_gate) _requestedFps = value; } }

    /// <summary>
    /// Frames por segundo em que o painel ficou em dia com a tela: enviados, ou
    /// identicos ao que o painel ja mostra. Frames descartados pelo controle de
    /// fluxo nao contam - e' por isso que este numero cai quando se pede mais FPS
    /// do que o enlace aguenta.
    /// </summary>
    public double EffectiveFps { get { lock (_gate) return _effectiveFps; } }

    public long FramesCaptured => Interlocked.Read(ref _captured);
    public long FramesProcessed => Interlocked.Read(ref _processed);
    public long FramesSent => Interlocked.Read(ref _sent);
    public long FramesSkippedUnchanged => Interlocked.Read(ref _skipped);
    public long CaptureErrors => Interlocked.Read(ref _captureErrors);
    public long LastPayloadBytes => Interlocked.Read(ref _lastPayloadBytes);

    /// <summary>Tempos medios por frame, em milissegundos.</summary>
    public double CaptureMs { get { lock (_gate) return _captureMs; } }
    public double ProcessMs { get { lock (_gate) return _processMs; } }
    public double EncodeMs { get { lock (_gate) return _encodeMs; } }
    public double SendMs { get { lock (_gate) return _sendMs; } }
    public double TotalMs { get { lock (_gate) return _totalMs; } }

    internal void SetEffectiveFps(double fps)
    {
        lock (_gate) _effectiveFps = fps;
    }

    internal void AddTimings(double capture, double process, double encode, double send)
    {
        lock (_gate)
        {
            const double A = 0.1;
            bool first = _totalMs <= 0;
            _captureMs = first ? capture : _captureMs * (1 - A) + capture * A;
            _processMs = first ? process : _processMs * (1 - A) + process * A;
            _encodeMs = first ? encode : _encodeMs * (1 - A) + encode * A;
            _sendMs = first ? send : _sendMs * (1 - A) + send * A;
            double total = capture + process + encode + send;
            _totalMs = first ? total : _totalMs * (1 - A) + total * A;
        }
    }

    internal void Reset()
    {
        Interlocked.Exchange(ref _captured, 0);
        Interlocked.Exchange(ref _processed, 0);
        Interlocked.Exchange(ref _sent, 0);
        Interlocked.Exchange(ref _skipped, 0);
        Interlocked.Exchange(ref _captureErrors, 0);
        Interlocked.Exchange(ref _lastPayloadBytes, 0);
        lock (_gate)
        {
            _effectiveFps = 0;
            _captureMs = _processMs = _encodeMs = _sendMs = _totalMs = 0;
        }
    }

    public PipelineStatistics Snapshot()
    {
        var s = new PipelineStatistics
        {
            _captured = FramesCaptured,
            _processed = FramesProcessed,
            _sent = FramesSent,
            _skipped = FramesSkippedUnchanged,
            _captureErrors = CaptureErrors,
            _lastPayloadBytes = LastPayloadBytes,
        };
        lock (_gate)
        {
            s._requestedFps = _requestedFps;
            s._effectiveFps = _effectiveFps;
            s._captureMs = _captureMs;
            s._processMs = _processMs;
            s._encodeMs = _encodeMs;
            s._sendMs = _sendMs;
            s._totalMs = _totalMs;
        }
        return s;
    }
}
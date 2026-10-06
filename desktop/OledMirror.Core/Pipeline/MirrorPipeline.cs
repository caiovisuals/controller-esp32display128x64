using System.Diagnostics;
using OledMirror.Core.Capture;
using OledMirror.Core.Devices;
using OledMirror.Core.Imaging;
using OledMirror.Core.Logging;
using OledMirror.Core.Protocol;

namespace OledMirror.Core.Pipeline;

/// <summary>
/// Captura -> processa -> codifica -> envia, no ritmo do FPS pedido, numa thread
/// propria. Nao aloca por frame.
///
/// Mantem o codificador coerente com o painel: quando um frame se perde (NACK,
/// ACK expirado, reconexao) o proximo vai completo; quando o controle de fluxo
/// recusa um frame, o codificador volta atras, porque o painel nao mudou.
/// </summary>
public sealed class MirrorPipeline : IDisposable
{
    private const string Cat = "pipeline";

    private readonly DeviceLink _link;
    private readonly Logger _log;
    private readonly FrameProcessor _processor = new();
    private readonly FrameEncoder _encoder = new();
    private readonly byte[] _frame = new byte[DisplayGeometry.FrameBytes];
    private readonly byte[] _latest = new byte[DisplayGeometry.FrameBytes];
    private readonly object _latestGate = new();
    private readonly object _sourceGate = new();
    private readonly object _lifecycleGate = new();

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _invalidate = true;
    private volatile bool _streamBeginPending = true;
    private volatile MirrorSettings _settings = new();
    private ICaptureSource? _source;
    private bool _hasLatest;
    private long _lastSentTimestamp;
    private int _sourceWidth, _sourceHeight;

    public MirrorPipeline(DeviceLink link, Logger? log = null)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _log = log ?? Logger.Null;

        _link.FrameLost += OnFrameLost;
        _link.DeviceIdentified += OnDeviceIdentified;
        _link.StateChanged += OnLinkStateChanged;
    }

    public MirrorSettings Settings
    {
        get => _settings;
        set => _settings = value ?? throw new ArgumentNullException(nameof(value));
    }

    public bool IsRunning => _thread is { IsAlive: true } && !_stop;

    public PipelineStatistics Statistics { get; } = new();

    public EncoderStatistics EncoderStatistics => _encoder.Statistics;

    /// <summary>Resolucao da fonte atual (0x0 se nao houver).</summary>
    public (int Width, int Height) SourceResolution
    {
        get { lock (_sourceGate) return (_sourceWidth, _sourceHeight); }
    }

    /// <summary>Um frame novo foi processado. Disparado na thread do pipeline.</summary>
    public event Action? FrameReady;

    /// <summary>Troca a fonte. A anterior e' liberada.</summary>
    public void SetSource(ICaptureSource? source)
    {
        ICaptureSource? previous;
        lock (_sourceGate)
        {
            previous = _source;
            _source = source;
            _sourceWidth = source?.SourceWidth ?? 0;
            _sourceHeight = source?.SourceHeight ?? 0;
        }
        if (previous is not null && !ReferenceEquals(previous, source)) previous.Dispose();
        if (source is not null) _log.Info(Cat, $"Fonte: {source.Name} ({source.SourceWidth}x{source.SourceHeight}).");
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_thread is { IsAlive: true }) return;

            _stop = false;
            _invalidate = true;
            _streamBeginPending = true;
            Statistics.Reset();
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "OledMirror pipeline",
                // Acima da UI para o ritmo nao tremer, mas nunca tempo real.
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }
        _log.Info(Cat, $"Espelhamento iniciado a {Settings.TargetFps} FPS.");
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lifecycleGate)
        {
            thread = _thread;
            if (thread is null) return;
            _thread = null;
            _stop = true;
        }

        if (thread != Thread.CurrentThread) thread.Join(3000);
        if (!Settings.PreviewOnly) _link.SendStreamEnd();
        _log.Info(Cat, "Espelhamento parado.");
    }

    /// <summary>Copia o ultimo frame processado (o que a previa mostra).</summary>
    public bool TryCopyLatestFrame(Span<byte> destination)
    {
        lock (_latestGate)
        {
            if (!_hasLatest) return false;
            _latest.CopyTo(destination);
            return true;
        }
    }

    public void Dispose()
    {
        Stop();
        _link.FrameLost -= OnFrameLost;
        _link.DeviceIdentified -= OnDeviceIdentified;
        _link.StateChanged -= OnLinkStateChanged;
        SetSource(null);
    }

    private void OnFrameLost()
    {
        _invalidate = true;
        // Um frame recusado pode significar que o ESP32 reiniciou ou voltou para a
        // tela de espera e saiu do modo streaming; o STREAM_BEGIN e' barato e idempotente.
        _streamBeginPending = true;
    }

    private void OnDeviceIdentified(DeviceHello device)
    {
        _invalidate = true;
        _streamBeginPending = true;
    }

    private void OnLinkStateChanged(LinkState state)
    {
        if (state != LinkState.Connected) _invalidate = true;
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        long nextTick = 0;
        long windowStart = clock.ElapsedTicks;
        int windowFrames = 0;
        long windowTicks = Stopwatch.Frequency / 2;

        try
        {
            while (!_stop)
            {
                MirrorSettings settings = _settings;
                int fps = Math.Clamp(settings.TargetFps, 1, 120);
                Statistics.RequestedFps = fps;
                long period = Stopwatch.Frequency / fps;

                WaitUntil(clock, nextTick);
                if (_stop) break;

                long now = clock.ElapsedTicks;
                // Se atrasou mais de um periodo, nao tenta "recuperar" frames: so segue o ritmo.
                nextTick = now - nextTick > period ? now + period : nextTick + period;

                if (Tick(settings)) windowFrames++;

                long elapsed = clock.ElapsedTicks - windowStart;
                if (elapsed >= windowTicks)
                {
                    Statistics.SetEffectiveFps(windowFrames * (double)Stopwatch.Frequency / elapsed);
                    windowFrames = 0;
                    windowStart = clock.ElapsedTicks;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(Cat, "O pipeline parou por um erro inesperado", ex);
        }
        finally
        {
            _stop = true;
        }
    }

    /// <summary>Um ciclo completo. Devolve true se o painel ficou em dia com a tela.</summary>
    private bool Tick(MirrorSettings settings)
    {
        long t0 = Stopwatch.GetTimestamp();

        CapturedFrame captured;
        bool ok;
        lock (_sourceGate)
        {
            if (_source is null) return false;
            try
            {
                ok = _source.TryCapture(out captured);
                _sourceWidth = _source.SourceWidth;
                _sourceHeight = _source.SourceHeight;
            }
            catch (Exception ex)
            {
                // Uma captura que falha nao pode parar o espelhamento.
                if (Interlocked.Increment(ref Statistics._captureErrors) == 1) _log.Warning(Cat, $"Falha na captura: {ex.Message}");
                return false;
            }

            if (!ok)
            {
                Interlocked.Increment(ref Statistics._captureErrors);
                return false;
            }
            Interlocked.Increment(ref Statistics._captured);

            long t1c = Stopwatch.GetTimestamp();
            _processor.Options = settings.Image;
            _processor.Process(captured, _frame);
            PublishTimings(t0, t1c, out double captureMs, out double processMs);
            Interlocked.Increment(ref Statistics._processed);

            lock (_latestGate)
            {
                _frame.CopyTo(_latest, 0);
                _hasLatest = true;
            }
            RaiseFrameReady();

            if (settings.PreviewOnly)
            {
                Statistics.AddTimings(captureMs, processMs, 0, 0);
                return true;
            }

            return Send(settings, captureMs, processMs);
        }
    }

    private static void PublishTimings(long start, long afterCapture, out double captureMs, out double processMs)
    {
        captureMs = Ms(start, afterCapture);
        processMs = Ms(afterCapture, Stopwatch.GetTimestamp());
    }

    private bool Send(MirrorSettings settings, double captureMs, double processMs)
    {
        if (_link.State != LinkState.Connected)
        {
            Statistics.AddTimings(captureMs, processMs, 0, 0);
            return false;
        }

        if (_streamBeginPending && _link.SendStreamBegin()) _streamBeginPending = false;

        DeviceHello? device = _link.Device;
        _encoder.Capabilities = device?.Capabilities ?? DeviceCapabilities.None;
        _encoder.Preference = settings.Encoding;
        _encoder.SkipUnchangedFrames = settings.SkipUnchangedFrames;

        long now = Stopwatch.GetTimestamp();
        bool keyframeDue = settings.KeyframeIntervalMs > 0 &&
                           now - _lastSentTimestamp > settings.KeyframeIntervalMs * Stopwatch.Frequency / 1000;

        if (_invalidate || keyframeDue)
        {
            _invalidate = false;
            _encoder.Invalidate();
        }

        long t0 = Stopwatch.GetTimestamp();
        if (!_encoder.TryEncode(_frame, out CommandId command, out ReadOnlyMemory<byte> payload))
        {
            Interlocked.Increment(ref Statistics._skipped);
            Statistics.AddTimings(captureMs, processMs, Ms(t0, Stopwatch.GetTimestamp()), 0);
            return true;
        }
        long t1 = Stopwatch.GetTimestamp();

        bool sent = _link.TrySendFrame(command, payload.Span);
        long t2 = Stopwatch.GetTimestamp();
        Statistics.AddTimings(captureMs, processMs, Ms(t0, t1), Ms(t1, t2));

        if (!sent)
        {
            // O frame nao foi para o fio: o painel continua com o anterior.
            _encoder.RollBack();
            return false;
        }

        _lastSentTimestamp = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref Statistics._sent);
        Interlocked.Exchange(ref Statistics._lastPayloadBytes, payload.Length);
        return true;
    }

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    private void RaiseFrameReady()
    {
        try { FrameReady?.Invoke(); }
        catch (Exception ex) { _log.Error(Cat, "Erro no handler de FrameReady", ex); }
    }

    /// <summary>Dorme ate perto do instante e termina com espera curta: o Sleep do Windows tem ~15 ms de granularidade.</summary>
    private void WaitUntil(Stopwatch clock, long tick)
    {
        while (!_stop)
        {
            long remaining = tick - clock.ElapsedTicks;
            if (remaining <= 0) return;

            double ms = remaining * 1000.0 / Stopwatch.Frequency;
            if (ms > 3) Thread.Sleep((int)Math.Min(ms - 2, 50));
            else Thread.Sleep(0);
        }
    }
}
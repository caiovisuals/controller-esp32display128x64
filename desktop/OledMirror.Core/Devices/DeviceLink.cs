using System.Diagnostics;
using System.Text;
using OledMirror.Core.Logging;
using OledMirror.Core.Protocol;
using OledMirror.Core.Transport;

namespace OledMirror.Core.Devices;

/// <summary>
/// A conexao com o ESP32: abre o transporte, faz o handshake, despacha as
/// respostas, controla o fluxo de frames e reconecta sozinho quando o cabo cai.
///
/// Tudo o que e' de longa duracao roda numa thread propria. Os metodos publicos
/// de envio podem ser chamados de qualquer thread; a escrita e' serializada por
/// um unico lock. Os eventos sao disparados na thread do link.
/// </summary>
public sealed class DeviceLink : IDisposable
{
    private const string Cat = "link";
    private const int ReadSliceMs = 20;
    private const int MaxInFlight = 32;

    private readonly Func<ITransport> _factory;
    private readonly Logger _log;
    private readonly DeviceLinkOptions _options;

    private readonly object _writeGate = new();
    private readonly object _flowGate = new();
    private readonly object _parserGate = new();
    private readonly object _lifecycleGate = new();

    private readonly PacketParser _parser = new();
    private readonly byte[] _txBuffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly byte[] _rxBuffer = new byte[4096];
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Janela de controle de fluxo: frames escritos e ainda sem FRAME_ACK, em ordem de envio.
    private readonly byte[] _inFlightSeq = new byte[MaxInFlight];
    private readonly long[] _inFlightTicks = new long[MaxInFlight];
    private int _inFlightHead;
    private int _inFlightCount;

    private Thread? _thread;
    private ITransport? _transport;
    private volatile bool _stopRequested;
    private volatile bool _reconnectRequested;
    private volatile bool _connectionBroken;
    private volatile int _state = (int)LinkState.Disconnected;
    private volatile DeviceHello? _device;
    private byte _txSequence;
    private uint _pingToken;

    public DeviceLink(Func<ITransport> transportFactory, Logger? log = null, DeviceLinkOptions? options = null)
    {
        _factory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _log = log ?? Logger.Null;
        _options = options ?? new DeviceLinkOptions();
    }

    public LinkState State => (LinkState)_state;

    /// <summary>O dispositivo identificado no ultimo handshake; null fora de uma conexao.</summary>
    public DeviceHello? Device => _device;

    public DeviceLinkOptions Options => _options;

    public LinkStatistics Statistics { get; } = new();

    public ParserStatistics ParserStatistics
    {
        get { lock (_parserGate) return _parser.Statistics.Snapshot(); }
    }

    /// <summary>Quantos frames podem estar sem confirmacao ao mesmo tempo.</summary>
    public int EffectiveWindow
    {
        get
        {
            int local = Math.Clamp(_options.MaxFramesInFlight, 1, MaxInFlight);
            int advertised = _device?.RxQueueDepth ?? 0;
            return advertised > 0 ? Math.Min(local, advertised) : local;
        }
    }

    public int FramesInFlight
    {
        get { lock (_flowGate) return _inFlightCount; }
    }

    public event Action<LinkState>? StateChanged;
    public event Action<DeviceHello>? DeviceIdentified;
    public event Action<DeviceStats>? StatsReceived;

    /// <summary>
    /// Um frame foi escrito mas nao chegou ao painel (NACK, ACK expirado ou
    /// queda). Quem gera deltas precisa mandar o proximo frame completo.
    /// </summary>
    public event Action? FrameLost;

    /// <summary>Mensagem LOG enviada pelo firmware.</summary>
    public event Action<DeviceLogLevel, string>? DeviceLog;

    // ------------------------------------------------------------------ ciclo de vida

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_thread is { IsAlive: true }) return;

            _stopRequested = false;
            _reconnectRequested = false;
            _wake.Reset();
            _thread = new Thread(Run) { IsBackground = true, Name = "OledMirror link" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_lifecycleGate)
        {
            thread = _thread;
            _thread = null;
            _stopRequested = true;
            _wake.Set();
        }

        if (thread is not null && thread != Thread.CurrentThread && !thread.Join(3000))
            _log.Warning(Cat, "A thread do link nao terminou a tempo.");

        CloseTransport();
        SetState(LinkState.Disconnected);
    }

    /// <summary>Derruba a conexao atual e reconecta (ou tenta de novo, se estava em falha).</summary>
    public void ForceReconnect()
    {
        _reconnectRequested = true;
        _wake.Set();

        lock (_lifecycleGate)
        {
            if (_thread is { IsAlive: true }) return;
        }
        Start();
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }

    // ------------------------------------------------------------------ envio

    /// <summary>
    /// Envia um frame se a janela de controle de fluxo permitir. Janela cheia
    /// devolve false e o frame e' descartado - de proposito: num espelhamento, o
    /// frame mais novo vale mais que uma fila do anterior.
    /// </summary>
    public bool TrySendFrame(CommandId command, ReadOnlySpan<byte> payload)
    {
        if (!command.IsFrame()) throw new ArgumentException($"{command} nao e' um comando de frame.", nameof(command));
        if (State != LinkState.Connected) return false;

        lock (_writeGate)
        {
            ITransport? transport = _transport;
            if (transport is null || _connectionBroken) return false;

            byte sequence = _txSequence;
            lock (_flowGate)
            {
                if (_inFlightCount >= EffectiveWindow)
                {
                    Interlocked.Increment(ref Statistics._framesDroppedByFlowControl);
                    return false;
                }
                int slot = (_inFlightHead + _inFlightCount) % MaxInFlight;
                _inFlightSeq[slot] = sequence;
                _inFlightTicks[slot] = _clock.ElapsedTicks;
                _inFlightCount++;
            }

            if (!WriteLocked(transport, command, payload))
            {
                lock (_flowGate) if (_inFlightCount > 0) _inFlightCount--;
                return false;
            }

            Interlocked.Increment(ref Statistics._framesSent);
            return true;
        }
    }

    public bool SendText(string text) => SendPacket(CommandId.Text, Payloads.BuildText(text));
    public bool SendClear() => SendPacket(CommandId.Clear, ReadOnlySpan<byte>.Empty);
    public bool SendStreamBegin() => SendPacket(CommandId.StreamBegin, stackalloc byte[] { 0 });
    public bool SendStreamEnd() => SendPacket(CommandId.StreamEnd, ReadOnlySpan<byte>.Empty);
    public bool SendConfig(byte[] tlv) => SendPacket(CommandId.SetConfig, tlv);
    public bool RequestStats() => SendPacket(CommandId.GetStats, ReadOnlySpan<byte>.Empty);
    public bool RequestInfo() => SendPacket(CommandId.GetInfo, ReadOnlySpan<byte>.Empty);
    public bool Ping() => SendPacket(CommandId.Ping, Payloads.BuildPing(unchecked(++_pingToken)));

    /// <summary>Envia um pacote de controle. Devolve false fora de uma conexao.</summary>
    public bool SendPacket(CommandId command, ReadOnlySpan<byte> payload)
    {
        lock (_writeGate)
        {
            ITransport? transport = _transport;
            if (transport is null || _connectionBroken) return false;
            return WriteLocked(transport, command, payload);
        }
    }

    private bool WriteLocked(ITransport transport, CommandId command, ReadOnlySpan<byte> payload)
    {
        int length = PacketEncoder.Encode(_txBuffer, command, _txSequence, payload);
        try
        {
            transport.Write(_txBuffer.AsSpan(0, length));
        }
        catch (Exception ex) when (ex is TransportException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            if (!_connectionBroken) _log.Warning(Cat, $"Falha de escrita: {ex.Message}");
            _connectionBroken = true;
            _wake.Set();
            return false;
        }

        _txSequence++;
        Interlocked.Increment(ref Statistics._packetsSent);
        Interlocked.Add(ref Statistics._bytesSent, length);
        return true;
    }

    // ------------------------------------------------------------------ thread do link

    private void Run()
    {
        int failures = 0;
        bool firstAttempt = true;

        while (!_stopRequested)
        {
            if (!firstAttempt) Interlocked.Increment(ref Statistics._reconnects);
            SetState(firstAttempt ? LinkState.Connecting : LinkState.Reconnecting);
            firstAttempt = false;
            _reconnectRequested = false;

            ITransport? transport = null;
            try
            {
                transport = _factory();
                transport.Open();
            }
            catch (Exception ex)
            {
                transport?.Dispose();
                _log.Warning(Cat, $"Nao foi possivel abrir o transporte: {ex.Message}");
                if (!ContinueAfterFailure(ref failures)) return;
                continue;
            }

            InstallTransport(transport);
            SetState(LinkState.Handshaking);
            _log.Debug(Cat, $"Handshake em {transport.Name}.");

            DeviceHello? hello = Handshake(transport);
            if (hello is null)
            {
                CloseTransport();
                if (_stopRequested) break;
                if (_reconnectRequested) continue;
                _log.Warning(Cat, $"Sem resposta ao HELLO em {transport.Name}. Firmware gravado? Baud correto? Monitor serial fechado?");
                if (!ContinueAfterFailure(ref failures)) return;
                continue;
            }

            failures = 0;
            _device = hello;
            _log.Info(Cat, $"Conectado: {hello}");
            if (hello.ProtocolVersion != ProtocolConstants.Version)
                _log.Warning(Cat, $"Firmware fala o protocolo v{hello.ProtocolVersion}; esta aplicacao fala o v{ProtocolConstants.Version}.");
            if (hello.Controller == ControllerId.Unknown)
                _log.Warning(Cat, "O ESP32 respondeu, mas o painel nao. Confira a fiacao (VCC no 3V3, SDA no GPIO21, SCL no GPIO22).");

            SetState(LinkState.Connected);
            Raise(() => DeviceIdentified?.Invoke(hello));

            string reason = RunConnected(transport);

            _device = null;
            CloseTransport();
            if (_stopRequested) break;

            _log.Warning(Cat, $"Conexao perdida: {reason}.");
            if (!_options.AutoReconnect && !_reconnectRequested)
            {
                SetState(LinkState.Disconnected);
                return;
            }

            // Mesmo numa reconexao pedida: o sistema precisa de um instante para
            // liberar a porta, e reabrir de imediato costuma dar "acesso negado".
            SetState(LinkState.Reconnecting);
            PauseBeforeReopen(_options.ReconnectDelayMs);
        }

        SetState(LinkState.Disconnected);
    }

    /// <summary>Espera o backoff. Devolve false quando e' para desistir.</summary>
    private bool ContinueAfterFailure(ref int failures)
    {
        if (_stopRequested) return false;
        if (!_options.AutoReconnect && !_reconnectRequested)
        {
            SetState(LinkState.Faulted);
            return false;
        }
        if (_reconnectRequested) return true;

        int delay = _options.ReconnectDelayMs;
        for (int i = 0; i < failures && delay < _options.MaxReconnectDelayMs; i++) delay *= 2;
        delay = Math.Min(delay, Math.Max(_options.ReconnectDelayMs, _options.MaxReconnectDelayMs));
        failures++;

        SleepInterruptibly(delay);
        return !_stopRequested;
    }

    private void SleepInterruptibly(int ms)
    {
        if (ms <= 0) return;
        _wake.Reset();
        if (_stopRequested || _reconnectRequested) return;
        _wake.Wait(ms);
    }

    /// <summary>Espera que so e' interrompida por Stop, nao por um pedido de reconexao.</summary>
    private void PauseBeforeReopen(int ms)
    {
        long deadline = _clock.ElapsedMilliseconds + Math.Max(0, ms);
        while (!_stopRequested)
        {
            long remaining = deadline - _clock.ElapsedMilliseconds;
            if (remaining <= 0) return;
            _wake.Reset();
            if (_stopRequested) return;
            _wake.Wait((int)remaining);
        }
    }

    private void InstallTransport(ITransport transport)
    {
        lock (_parserGate) _parser.Reset();
        ClearInFlight();
        lock (_writeGate)
        {
            _transport = transport;
            _connectionBroken = false;
        }
    }

    private void CloseTransport()
    {
        ITransport? transport;
        lock (_writeGate)
        {
            transport = _transport;
            _transport = null;
        }
        if (transport is null) return;

        try { transport.Dispose(); }
        catch (Exception ex) { _log.Debug(Cat, $"Erro ao fechar o transporte: {ex.Message}"); }

        if (ClearInFlight() > 0) Raise(() => FrameLost?.Invoke());
    }

    private DeviceHello? Handshake(ITransport transport)
    {
        byte[] hello = Payloads.BuildHello();
        var preamble = new byte[Math.Max(0, _options.SyncPreambleBytes)];

        for (int attempt = 1; attempt <= Math.Max(1, _options.HandshakeAttempts); attempt++)
        {
            if (_stopRequested || _reconnectRequested) return null;

            try
            {
                // Zeros desalinham um parser preso no meio de um pacote (ou no log de boot);
                // o SYNC confirma que o canal esta limpo.
                lock (_writeGate) transport.Write(preamble);
            }
            catch (Exception ex) when (ex is TransportException or IOException or InvalidOperationException)
            {
                _log.Warning(Cat, $"Falha de escrita no handshake: {ex.Message}");
                return null;
            }

            if (!SendPacket(CommandId.Sync, ReadOnlySpan<byte>.Empty) || !SendPacket(CommandId.Hello, hello)) return null;

            long deadline = _clock.ElapsedMilliseconds + Math.Max(50, _options.HandshakeTimeoutMs);
            while (_clock.ElapsedMilliseconds < deadline)
            {
                if (_stopRequested || _reconnectRequested) return null;

                int n;
                try { n = transport.Read(_rxBuffer, ReadSliceMs); }
                catch (TransportException ex)
                {
                    _log.Warning(Cat, $"Falha de leitura no handshake: {ex.Message}");
                    return null;
                }
                if (n <= 0) continue;

                lock (_parserGate) _parser.Push(_rxBuffer.AsSpan(0, n));
                while (TryDequeue(out Packet packet))
                {
                    if (packet.Command is CommandId.HelloAck or CommandId.Info && DeviceHello.TryParse(packet.Payload, out DeviceHello device))
                        return device;
                    if (packet.Command == CommandId.Log) HandleLog(packet);
                }
            }

            _log.Debug(Cat, $"Handshake: tentativa {attempt} sem resposta.");
        }

        return null;
    }

    private bool TryDequeue(out Packet packet)
    {
        lock (_parserGate) return _parser.TryDequeue(out packet);
    }

    private string RunConnected(ITransport transport)
    {
        long lastRx = _clock.ElapsedMilliseconds;
        long lastPing = lastRx;

        while (!_stopRequested)
        {
            if (_reconnectRequested) return "reconexao solicitada";
            if (_connectionBroken) return "erro de escrita";

            int n;
            try { n = transport.Read(_rxBuffer, ReadSliceMs); }
            catch (TransportException ex) { return ex.Message; }

            long now = _clock.ElapsedMilliseconds;
            if (n > 0)
            {
                lastRx = now;
                lock (_parserGate) _parser.Push(_rxBuffer.AsSpan(0, n));
                while (TryDequeue(out Packet packet)) Dispatch(packet);
            }

            if (now - lastRx > _options.InactivityTimeoutMs)
                return $"nenhuma resposta do dispositivo por {_options.InactivityTimeoutMs} ms";

            if (now - lastRx >= _options.KeepAliveIntervalMs && now - lastPing >= _options.KeepAliveIntervalMs)
            {
                lastPing = now;
                Ping();
            }

            CheckAckTimeouts();
        }

        return "parado";
    }

    private void Dispatch(Packet packet)
    {
        Interlocked.Increment(ref Statistics._packetsReceived);

        switch (packet.Command)
        {
            case CommandId.FrameAck:
                if (packet.Payload.Length >= 1) HandleFrameAck(packet.Payload[0]);
                break;

            case CommandId.Nack:
            {
                Interlocked.Increment(ref Statistics._nacks);
                var command = packet.Payload.Length > 0 ? (CommandId)packet.Payload[0] : 0;
                var reason = packet.Payload.Length > 1 ? (NackReason)packet.Payload[1] : NackReason.None;
                if (command.IsFrame())
                {
                    _log.Debug(Cat, $"NACK do frame seq {packet.Sequence}: {reason}.");
                    if (RemoveInFlight(packet.Sequence) >= 0) Raise(() => FrameLost?.Invoke());
                }
                else
                {
                    _log.Warning(Cat, $"O dispositivo recusou {command}: {reason}.");
                }
                break;
            }

            case CommandId.Stats:
                if (DeviceStats.TryParse(packet.Payload, out DeviceStats stats)) Raise(() => StatsReceived?.Invoke(stats));
                break;

            case CommandId.Info:
            case CommandId.HelloAck:
                if (DeviceHello.TryParse(packet.Payload, out DeviceHello hello)) _device = hello;
                break;

            case CommandId.Log:
                HandleLog(packet);
                break;

            case CommandId.Ack:
            case CommandId.Pong:
                break;

            default:
                _log.Debug(Cat, $"Pacote inesperado do dispositivo: {packet}.");
                break;
        }
    }

    private void HandleLog(Packet packet)
    {
        if (packet.Payload.Length == 0) return;
        var level = (DeviceLogLevel)packet.Payload[0];
        string text = Encoding.UTF8.GetString(packet.Payload, 1, packet.Payload.Length - 1);

        switch (level)
        {
            case DeviceLogLevel.Error:
                Interlocked.Increment(ref Statistics._deviceErrors);
                _log.Error("esp32", text);
                break;
            case DeviceLogLevel.Warning: _log.Warning("esp32", text); break;
            case DeviceLogLevel.Info: _log.Info("esp32", text); break;
            default: _log.Debug("esp32", text); break;
        }
        Raise(() => DeviceLog?.Invoke(level, text));
    }

    private void HandleFrameAck(byte sequence)
    {
        long sentTicks;
        int lost;
        lock (_flowGate)
        {
            int index = IndexOfInFlight(sequence);
            if (index < 0) return;   // ACK atrasado de um frame que ja expirou

            int slot = (_inFlightHead + index) % MaxInFlight;
            sentTicks = _inFlightTicks[slot];
            lost = index;
            // O dispositivo processa em ordem: o que ficou antes deste ACK se perdeu no caminho.
            _inFlightHead = (_inFlightHead + index + 1) % MaxInFlight;
            _inFlightCount -= index + 1;
        }

        Interlocked.Increment(ref Statistics._framesAcked);
        Statistics.AddLatency((_clock.ElapsedTicks - sentTicks) * 1000.0 / Stopwatch.Frequency);

        if (lost > 0)
        {
            Interlocked.Add(ref Statistics._framesLost, lost);
            Raise(() => FrameLost?.Invoke());
        }
    }

    /// <summary>Remove a entrada com esta sequencia. Devolve o indice que ela tinha, ou -1.</summary>
    private int RemoveInFlight(byte sequence)
    {
        lock (_flowGate)
        {
            int index = IndexOfInFlight(sequence);
            if (index < 0) return -1;

            for (int i = index; i < _inFlightCount - 1; i++)
            {
                int to = (_inFlightHead + i) % MaxInFlight;
                int from = (_inFlightHead + i + 1) % MaxInFlight;
                _inFlightSeq[to] = _inFlightSeq[from];
                _inFlightTicks[to] = _inFlightTicks[from];
            }
            _inFlightCount--;
            Interlocked.Increment(ref Statistics._framesLost);
            return index;
        }
    }

    private int IndexOfInFlight(byte sequence)
    {
        for (int i = 0; i < _inFlightCount; i++)
            if (_inFlightSeq[(_inFlightHead + i) % MaxInFlight] == sequence) return i;
        return -1;
    }

    private void CheckAckTimeouts()
    {
        long limit = (long)(_options.FrameAckTimeoutMs * (Stopwatch.Frequency / 1000.0));
        bool expired = false;

        lock (_flowGate)
        {
            long now = _clock.ElapsedTicks;
            while (_inFlightCount > 0 && now - _inFlightTicks[_inFlightHead] > limit)
            {
                _inFlightHead = (_inFlightHead + 1) % MaxInFlight;
                _inFlightCount--;
                Interlocked.Increment(ref Statistics._frameAckTimeouts);
                Interlocked.Increment(ref Statistics._framesLost);
                expired = true;
            }
        }

        if (expired)
        {
            _log.Debug(Cat, "FRAME_ACK nao chegou a tempo; o proximo frame vai completo.");
            Raise(() => FrameLost?.Invoke());
        }
    }

    private int ClearInFlight()
    {
        lock (_flowGate)
        {
            int count = _inFlightCount;
            _inFlightHead = 0;
            _inFlightCount = 0;
            return count;
        }
    }

    private void SetState(LinkState state)
    {
        int previous = Interlocked.Exchange(ref _state, (int)state);
        if (previous == (int)state) return;
        _log.Debug(Cat, $"Estado: {(LinkState)previous} -> {state}");
        Raise(() => StateChanged?.Invoke(state));
    }

    /// <summary>Um handler com erro nao pode derrubar a thread do link.</summary>
    private void Raise(Action action)
    {
        try { action(); }
        catch (Exception ex) { _log.Error(Cat, "Erro num handler de evento do link", ex); }
    }
}
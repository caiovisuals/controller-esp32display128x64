namespace OledMirror.Core.Devices;

public enum LinkState
{
    Disconnected,
    Connecting,
    Handshaking,
    Connected,
    Reconnecting,
    /// <summary>Desistiu de conectar (reconexao automatica desligada).</summary>
    Faulted,
}

public sealed class DeviceLinkOptions
{
    /// <summary>Quanto esperar pelo HELLO_ACK em cada tentativa.</summary>
    public int HandshakeTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Tentativas de handshake por conexao. Mais de uma cobre o caso de a placa
    /// reiniciar ao abrir a porta e ainda estar no boot quando o primeiro HELLO chega.
    /// </summary>
    public int HandshakeAttempts { get; set; } = 3;

    public bool AutoReconnect { get; set; } = true;

    /// <summary>Primeiro intervalo de reconexao; dobra a cada falha seguida.</summary>
    public int ReconnectDelayMs { get; set; } = 500;
    public int MaxReconnectDelayMs { get; set; } = 5000;

    /// <summary>Sem nada recebido por este tempo, manda um PING.</summary>
    public int KeepAliveIntervalMs { get; set; } = 2000;

    /// <summary>Sem nada recebido por este tempo, o link e' dado como caido.</summary>
    public int InactivityTimeoutMs { get; set; } = 4000;

    /// <summary>Um frame sem FRAME_ACK por este tempo e' dado como perdido.</summary>
    public int FrameAckTimeoutMs { get; set; } = 1000;

    /// <summary>Teto local da janela; a janela efetiva e' o menor entre isto e a fila anunciada pelo dispositivo.</summary>
    public int MaxFramesInFlight { get; set; } = 4;

    /// <summary>Bytes 0x00 enviados antes do SYNC para desalinhar um parser preso no meio de um pacote.</summary>
    public int SyncPreambleBytes { get; set; } = 64;
}

/// <summary>Contadores do link. Atualizados de varias threads; leia com <see cref="Snapshot"/>.</summary>
public sealed class LinkStatistics
{
    internal long _packetsSent, _bytesSent, _framesSent, _framesAcked, _framesDroppedByFlowControl;
    internal long _frameAckTimeouts, _framesLost, _nacks, _reconnects, _deviceErrors, _packetsReceived;
    private double _lastLatencyMs, _averageLatencyMs;
    private readonly object _latencyGate = new();

    public long PacketsSent => Interlocked.Read(ref _packetsSent);
    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long FramesAcked => Interlocked.Read(ref _framesAcked);

    /// <summary>Frames recusados porque a janela de controle de fluxo estava cheia.</summary>
    public long FramesDroppedByFlowControl => Interlocked.Read(ref _framesDroppedByFlowControl);

    public long FrameAckTimeouts => Interlocked.Read(ref _frameAckTimeouts);

    /// <summary>Frames escritos no fio que nao chegaram ao painel (timeout, NACK ou ACK pulado).</summary>
    public long FramesLost => Interlocked.Read(ref _framesLost);

    public long Nacks => Interlocked.Read(ref _nacks);

    /// <summary>Tentativas de reconexao (inclui as que falharam).</summary>
    public long Reconnects => Interlocked.Read(ref _reconnects);

    /// <summary>Mensagens de erro enviadas pelo proprio dispositivo (LOG nivel erro).</summary>
    public long DeviceErrors => Interlocked.Read(ref _deviceErrors);

    /// <summary>Do envio ao FRAME_ACK, que o firmware manda depois de o frame estar no painel.</summary>
    public double LastLatencyMs { get { lock (_latencyGate) return _lastLatencyMs; } }
    public double AverageLatencyMs { get { lock (_latencyGate) return _averageLatencyMs; } }

    internal void AddLatency(double ms)
    {
        lock (_latencyGate)
        {
            _lastLatencyMs = ms;
            _averageLatencyMs = _averageLatencyMs <= 0 ? ms : _averageLatencyMs * 0.9 + ms * 0.1;
        }
    }

    public LinkStatistics Snapshot()
    {
        var s = new LinkStatistics
        {
            _packetsSent = PacketsSent,
            _packetsReceived = PacketsReceived,
            _bytesSent = BytesSent,
            _framesSent = FramesSent,
            _framesAcked = FramesAcked,
            _framesDroppedByFlowControl = FramesDroppedByFlowControl,
            _frameAckTimeouts = FrameAckTimeouts,
            _framesLost = FramesLost,
            _nacks = Nacks,
            _reconnects = Reconnects,
            _deviceErrors = DeviceErrors,
        };
        lock (_latencyGate)
        {
            s._lastLatencyMs = _lastLatencyMs;
            s._averageLatencyMs = _averageLatencyMs;
        }
        return s;
    }
}
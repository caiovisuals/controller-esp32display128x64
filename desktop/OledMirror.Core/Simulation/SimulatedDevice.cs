using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using OledMirror.Core.Devices;
using OledMirror.Core.Imaging;
using OledMirror.Core.Logging;
using OledMirror.Core.Protocol;
using OledMirror.Core.Transport;

namespace OledMirror.Core.Simulation;

public sealed class SimulatedDeviceOptions
{
    /// <summary>Profundidade de fila anunciada no HELLO_ACK (a do firmware e' 2).</summary>
    public int RxQueueDepth { get; set; } = 2;

    /// <summary>Espera o tempo real de escrita no painel antes do FRAME_ACK.</summary>
    public bool SimulateRenderDelay { get; set; }

    /// <summary>1024 bytes em I2C a 800 kHz: ~11,5 ms.</summary>
    public double RenderDelayMs { get; set; } = 11.5;

    /// <summary>Aplica os frames mas nunca confirma: simula FRAME_ACKs perdidos no caminho.</summary>
    public bool SuppressFrameAcks { get; set; }

    public ControllerId Controller { get; set; } = ControllerId.Ssd1306;
    public DeviceCapabilities Capabilities { get; set; } = DeviceCapabilities.All;
    public string Name { get; set; } = "OledMirror simulado";
}

/// <summary>
/// Um ESP32 de mentira: mesma maquina de estados do firmware
/// (firmware/src/app/session.cpp), rodando numa thread, sobre um transporte
/// qualquer. Permite testar o host inteiro sem hardware.
/// </summary>
public sealed class SimulatedDevice : IDisposable
{
    private const string Cat = "sim";

    private readonly ITransport _transport;
    private readonly Logger _log;
    private readonly SimulatedDeviceOptions _options;
    private readonly PacketParser _parser = new();
    private readonly FrameDecoder _decoder = new();
    private readonly byte[] _framebuffer = new byte[DisplayGeometry.FrameBytes];
    private readonly object _framebufferGate = new();
    private readonly byte[] _rx = new byte[4096];
    private readonly byte[] _tx = new byte[ProtocolConstants.MaxPacketSize];
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private Thread? _thread;
    private volatile bool _stop;
    private byte _txSequence;
    private uint _framesApplied;
    private uint _framesDropped;
    private ushort _lastRenderMicros;

    public SimulatedDevice(ITransport transport, Logger? log = null, SimulatedDeviceOptions? options = null)
    {
        _transport = transport;
        _log = log ?? Logger.Null;
        _options = options ?? new SimulatedDeviceOptions();
    }

    public uint FramesApplied => Volatile.Read(ref _framesApplied);
    public uint FramesDropped => Volatile.Read(ref _framesDropped);
    public bool IsStreaming { get; private set; }
    public byte Contrast { get; private set; } = 0x7F;
    public bool Inverted { get; private set; }
    public string? LastText { get; private set; }

    public byte[] SnapshotFramebuffer()
    {
        lock (_framebufferGate) return (byte[])_framebuffer.Clone();
    }

    /// <summary>
    /// Simula o ESP32 reiniciando com o cabo ainda ligado: o painel perde o
    /// conteudo, mas o link continua respondendo normalmente.
    /// </summary>
    public void SimulateReset()
    {
        lock (_framebufferGate) Array.Clear(_framebuffer);
    }

    public void Start()
    {
        if (_thread is not null) return;
        _transport.Open();
        _thread = new Thread(Run) { IsBackground = true, Name = "OledMirror dispositivo simulado" };
        _thread.Start();
    }

    /// <summary>Para o dispositivo e fecha a sua ponta, como se o cabo fosse arrancado.</summary>
    public void Stop()
    {
        _stop = true;
        _transport.Dispose();
        Thread? thread = _thread;
        if (thread is not null && thread != Thread.CurrentThread) thread.Join(2000);
    }

    public void Dispose() => Stop();

    private void Run()
    {
        try
        {
            while (!_stop)
            {
                int n = _transport.Read(_rx, 20);
                if (n <= 0) continue;

                _parser.Push(_rx.AsSpan(0, n));
                while (_parser.TryDequeue(out Packet packet)) Handle(packet);
            }
        }
        catch (TransportException)
        {
            // Host fechou a ponta dele: fim da sessao.
        }
        catch (Exception ex)
        {
            _log.Error(Cat, "Dispositivo simulado falhou", ex);
        }
    }

    private void Handle(Packet packet)
    {
        switch (packet.Command)
        {
            case CommandId.Hello: Send(CommandId.HelloAck, BuildHello()); break;
            case CommandId.GetInfo: Send(CommandId.Info, BuildHello()); break;
            case CommandId.Ping: Send(CommandId.Pong, packet.Payload); break;
            case CommandId.Sync: Ack(CommandId.Sync); break;

            case CommandId.StreamBegin:
                IsStreaming = true;
                Ack(CommandId.StreamBegin);
                break;

            case CommandId.StreamEnd:
                IsStreaming = false;
                Ack(CommandId.StreamEnd);
                break;

            case CommandId.Clear:
                lock (_framebufferGate) Array.Clear(_framebuffer);
                Ack(CommandId.Clear);
                break;

            case CommandId.Text:
                LastText = Encoding.UTF8.GetString(packet.Payload);
                Ack(CommandId.Text);
                break;

            case CommandId.SetConfig:
                if (ApplyConfig(packet.Payload)) Ack(CommandId.SetConfig);
                else Nack(packet.Command, NackReason.BadPayload, packet.Sequence);
                break;

            case CommandId.GetStats:
                Send(CommandId.Stats, new DeviceStats
                {
                    FramesApplied = FramesApplied,
                    FramesDropped = FramesDropped,
                    CrcErrors = (uint)(_parser.Statistics.HeaderCrcErrors + _parser.Statistics.PayloadCrcErrors),
                    Resyncs = (uint)_parser.Statistics.Resyncs,
                    LastRenderMicros = _lastRenderMicros,
                    FreeHeapKb = 200,
                    UptimeSeconds = (uint)_uptime.Elapsed.TotalSeconds,
                }.ToPayload());
                break;

            case CommandId.FrameRaw:
            case CommandId.FrameRle:
            case CommandId.FrameDelta:
            case CommandId.FrameDeltaRle:
                HandleFrame(packet);
                break;

            default:
                Nack(packet.Command, NackReason.UnknownCommand, packet.Sequence);
                break;
        }
    }

    private void HandleFrame(Packet packet)
    {
        bool supported = packet.Command switch
        {
            CommandId.FrameRle => _options.Capabilities.HasFlag(DeviceCapabilities.Rle),
            CommandId.FrameDelta => _options.Capabilities.HasFlag(DeviceCapabilities.Delta),
            CommandId.FrameDeltaRle => _options.Capabilities.HasFlag(DeviceCapabilities.Delta | DeviceCapabilities.Rle),
            _ => true,
        };
        if (!supported)
        {
            Interlocked.Increment(ref _framesDropped);
            Nack(packet.Command, NackReason.UnknownCommand, packet.Sequence);
            return;
        }

        long started = Stopwatch.GetTimestamp();
        FrameDecodeResult result;
        lock (_framebufferGate) result = _decoder.Apply(packet.Command, packet.Payload, _framebuffer);

        if (result != FrameDecodeResult.Ok)
        {
            Interlocked.Increment(ref _framesDropped);
            Nack(packet.Command, FrameDecoder.NackReasonFor(result), packet.Sequence);
            return;
        }

        if (_options.SimulateRenderDelay)
        {
            // Espera ativa curta: Thread.Sleep tem granularidade de ~15 ms no Windows.
            long until = started + (long)(_options.RenderDelayMs * Stopwatch.Frequency / 1000.0);
            while (Stopwatch.GetTimestamp() < until) Thread.Sleep(0);
        }

        double elapsedUs = (Stopwatch.GetTimestamp() - started) * 1_000_000.0 / Stopwatch.Frequency;
        _lastRenderMicros = (ushort)Math.Min(ushort.MaxValue, elapsedUs);
        Interlocked.Increment(ref _framesApplied);

        if (_options.SuppressFrameAcks) return;

        Span<byte> ack = stackalloc byte[5];
        ack[0] = packet.Sequence;
        ack[1] = (byte)_options.RxQueueDepth;
        BinaryPrimitives.WriteUInt16LittleEndian(ack[2..], _lastRenderMicros);
        ack[4] = 0;
        Send(CommandId.FrameAck, ack);
    }

    private bool ApplyConfig(ReadOnlySpan<byte> tlv)
    {
        int offset = 0;
        while (offset < tlv.Length)
        {
            if (offset + 2 > tlv.Length) return false;
            var key = (ConfigKey)tlv[offset];
            int length = tlv[offset + 1];
            if (offset + 2 + length > tlv.Length) return false;
            ReadOnlySpan<byte> value = tlv.Slice(offset + 2, length);

            switch (key)
            {
                case ConfigKey.Contrast when length >= 1: Contrast = value[0]; break;
                case ConfigKey.Invert when length >= 1: Inverted = value[0] != 0; break;
                case ConfigKey.Contrast or ConfigKey.Invert or ConfigKey.FlipVertical or ConfigKey.FlipHorizontal
                     or ConfigKey.DisplayOn or ConfigKey.Controller when length < 1:
                    return false;
                case ConfigKey.IdleTimeoutMs when length < 2:
                    return false;
            }
            // Chaves desconhecidas sao ignoradas, como no firmware.
            offset += 2 + length;
        }
        return true;
    }

    private byte[] BuildHello() => new DeviceHello
    {
        ProtocolVersion = ProtocolConstants.Version,
        FirmwareMajor = 1,
        FirmwareMinor = 0,
        FirmwarePatch = 0,
        Capabilities = _options.Capabilities,
        DisplayWidth = DisplayGeometry.Width,
        DisplayHeight = DisplayGeometry.Height,
        Controller = _options.Controller,
        Bus = BusId.I2c,
        I2cAddress = 0x3C,
        RxQueueDepth = _options.RxQueueDepth,
        Name = _options.Name,
    }.ToPayload();

    private void Ack(CommandId command) => Send(CommandId.Ack, stackalloc byte[] { (byte)command });

    private void Nack(CommandId command, NackReason reason, byte sequence)
    {
        Span<byte> payload = stackalloc byte[] { (byte)command, (byte)reason };
        int n = PacketEncoder.Encode(_tx, CommandId.Nack, sequence, payload);
        _transport.Write(_tx.AsSpan(0, n));
    }

    private void Send(CommandId command, ReadOnlySpan<byte> payload)
    {
        int n = PacketEncoder.Encode(_tx, command, _txSequence++, payload);
        _transport.Write(_tx.AsSpan(0, n));
    }
}
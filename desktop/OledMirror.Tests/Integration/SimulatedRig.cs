using OledMirror.Core.Devices;
using OledMirror.Core.Logging;
using OledMirror.Core.Simulation;
using OledMirror.Core.Transport;

namespace OledMirror.Tests.Integration;

/// <summary>
/// Monta host + dispositivo simulado ligados por um loopback. Cada
/// (re)conexao do <see cref="DeviceLink"/> cria um par novo e um dispositivo
/// novo, exatamente como acontece quando a placa e' desconectada e religada.
/// </summary>
internal sealed class SimulatedRig : IDisposable {
    private readonly List<SimulatedDevice> _devices = new();
    private readonly object _gate = new();
    private readonly SimulatedDeviceOptions _deviceOptions;

    public SimulatedRig(DeviceLinkOptions? linkOptions = null, SimulatedDeviceOptions? deviceOptions = null,
                        double corruptionRate = 0, Logger? log = null)
    {
        _deviceOptions = deviceOptions ?? new SimulatedDeviceOptions();
        CorruptionRate = corruptionRate;

        Link = new DeviceLink(CreateTransport, log ?? Logger.Null, linkOptions ?? new DeviceLinkOptions
        {
            HandshakeTimeoutMs = 1000,
            HandshakeAttempts = 3,
            ReconnectDelayMs = 50,
            KeepAliveIntervalMs = 250,
            InactivityTimeoutMs = 2000,
        });
    }

    public DeviceLink Link { get; }
    public double CorruptionRate { get; set; }

    /// <summary>Faz a proxima tentativa de conexao falhar (simula placa ausente).</summary>
    public bool FailNextConnection { get; set; }

    public SimulatedDevice? CurrentDevice
    {
        get { lock (_gate) return _devices.Count == 0 ? null : _devices[^1]; }
    }

    private ITransport CreateTransport()
    {
        if (FailNextConnection) throw new TransportException("Porta nao encontrada (simulado).");

        (LoopbackTransport host, LoopbackTransport device) = LoopbackTransport.CreatePair(Random.Shared.Next());
        host.ByteCorruptionRate = CorruptionRate;
        device.ByteCorruptionRate = CorruptionRate;

        var simulated = new SimulatedDevice(device, Logger.Null, _deviceOptions);
        simulated.Start();
        lock (_gate) _devices.Add(simulated);

        return host;
    }

    /// <summary>Derruba o link atual, como se o cabo USB fosse arrancado.</summary>
    public void YankCable()
    {
        SimulatedDevice? device = CurrentDevice;
        device?.Stop();
        Link.ForceReconnect();
    }

    public static bool WaitFor(Func<bool> condition, int timeoutMs = 4000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    public bool WaitForConnected(int timeoutMs = 4000) => WaitFor(() => Link.State == LinkState.Connected, timeoutMs);

    public void Dispose()
    {
        Link.Dispose();
        lock (_gate) foreach (SimulatedDevice d in _devices) d.Dispose();
    }
}
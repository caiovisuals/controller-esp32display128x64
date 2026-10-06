namespace OledMirror.Core.Transport;

/// <summary>
/// Par de transportes ligados em memoria, como as duas pontas de um cabo. Usado
/// para rodar o host contra o dispositivo simulado. Pode corromper bytes de
/// proposito para exercitar a recuperacao de erros.
/// </summary>
public sealed class LoopbackTransport : ITransport
{
    private readonly ByteQueue _inbound;
    private readonly ByteQueue _outbound;
    private readonly Random _random;
    private readonly object _randomGate = new();
    private volatile bool _disposed;

    private LoopbackTransport(string name, ByteQueue inbound, ByteQueue outbound, Random random)
    {
        Name = name;
        _inbound = inbound;
        _outbound = outbound;
        _random = random;
    }

    /// <summary>Cria as duas pontas: o que uma escreve, a outra le.</summary>
    public static (LoopbackTransport Host, LoopbackTransport Device) CreatePair(int? seed = null)
    {
        var toDevice = new ByteQueue();
        var toHost = new ByteQueue();
        int s = seed ?? Random.Shared.Next();
        var host = new LoopbackTransport("loopback (host)", toHost, toDevice, new Random(s));
        var device = new LoopbackTransport("loopback (dispositivo)", toDevice, toHost, new Random(s ^ 0x5A5A5A5A));
        return (host, device);
    }

    public string Name { get; }
    public bool IsOpen => !_disposed && !_inbound.Closed;

    /// <summary>Probabilidade, por byte escrito, de um bit ser trocado no caminho.</summary>
    public double ByteCorruptionRate { get; set; }

    public void Open()
    {
        if (_disposed || _inbound.Closed) throw new TransportException("Loopback fechado.");
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_disposed) throw new TransportException("Loopback fechado.");
        return _inbound.Read(buffer, timeoutMs);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed) throw new TransportException("Loopback fechado.");

        double rate = ByteCorruptionRate;
        if (rate <= 0)
        {
            _outbound.Write(data, corrupt: null);
            return;
        }

        _outbound.Write(data, (value) =>
        {
            lock (_randomGate)
            {
                return _random.NextDouble() < rate ? (byte)(value ^ (1 << _random.Next(8))) : value;
            }
        });
    }

    public void DiscardInput() => _inbound.Clear();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Fecha as duas direcoes: a outra ponta percebe a queda na proxima leitura ou escrita.
        _inbound.Close();
        _outbound.Close();
    }

    /// <summary>Fila circular de bytes com espera. Cresce sob demanda.</summary>
    private sealed class ByteQueue
    {
        private readonly object _gate = new();
        private byte[] _buffer = new byte[16 * 1024];
        private int _head;
        private int _count;

        public bool Closed { get; private set; }

        public void Write(ReadOnlySpan<byte> data, Func<byte, byte>? corrupt)
        {
            lock (_gate)
            {
                if (Closed) throw new TransportException("A outra ponta do loopback foi fechada.");
                EnsureCapacity(_count + data.Length);
                for (int i = 0; i < data.Length; i++)
                {
                    byte b = corrupt is null ? data[i] : corrupt(data[i]);
                    _buffer[(_head + _count) % _buffer.Length] = b;
                    _count++;
                }
                Monitor.PulseAll(_gate);
            }
        }

        public int Read(Span<byte> destination, int timeoutMs)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    if (Closed) throw new TransportException("A outra ponta do loopback foi fechada.");
                    long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
                    while (_count == 0 && !Closed)
                    {
                        long remaining = deadline - Environment.TickCount64;
                        if (remaining <= 0) return 0;
                        Monitor.Wait(_gate, (int)remaining);
                    }
                    if (_count == 0) throw new TransportException("A outra ponta do loopback foi fechada.");
                }

                int n = Math.Min(destination.Length, _count);
                for (int i = 0; i < n; i++) destination[i] = _buffer[(_head + i) % _buffer.Length];
                _head = (_head + n) % _buffer.Length;
                _count -= n;
                return n;
            }
        }

        public void Clear()
        {
            lock (_gate) { _head = 0; _count = 0; }
        }

        public void Close()
        {
            lock (_gate)
            {
                Closed = true;
                Monitor.PulseAll(_gate);
            }
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length) return;
            int size = _buffer.Length;
            while (size < required) size *= 2;
            var bigger = new byte[size];
            for (int i = 0; i < _count; i++) bigger[i] = _buffer[(_head + i) % _buffer.Length];
            _buffer = bigger;
            _head = 0;
        }
    }
}
using System.IO.Ports;

namespace OledMirror.Core.Transport;

/// <summary>Erro de I/O no transporte: porta ausente, ocupada ou desconectada.</summary>
public sealed class TransportException : Exception
{
    public TransportException(string message) : base(message) { }
    public TransportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Canal de bytes ate o dispositivo.</summary>
public interface ITransport : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }

    /// <summary>Abre o canal. Lanca <see cref="TransportException"/> se nao for possivel.</summary>
    void Open();

    /// <summary>
    /// Le o que estiver disponivel, esperando no maximo <paramref name="timeoutMs"/>.
    /// Devolve 0 se nada chegou. Lanca <see cref="TransportException"/> se o canal caiu.
    /// </summary>
    int Read(Span<byte> buffer, int timeoutMs);

    /// <summary>Escreve tudo. Lanca <see cref="TransportException"/> se o canal caiu.</summary>
    void Write(ReadOnlySpan<byte> data);

    void DiscardInput();
}

public sealed class SerialTransportOptions
{
    public string PortName { get; set; } = "COM3";
    public int BaudRate { get; set; } = 921600;
    public int WriteTimeoutMs { get; set; } = 500;

    /// <summary>
    /// DTR e RTS ficam desligados: nas placas DevKit eles controlam o EN e o
    /// GPIO0 pelo circuito de auto-reset, e liga-los pode reiniciar o ESP32 ou
    /// deixa-lo preso no modo de gravacao.
    /// </summary>
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }
}

/// <summary>Transporte sobre System.IO.Ports.</summary>
public sealed class SerialPortTransport : ITransport
{
    private readonly SerialTransportOptions _options;
    private SerialPort? _port;

    public SerialPortTransport(SerialTransportOptions options)
    {
        _options = options;
    }

    public string Name => $"{_options.PortName} @ {_options.BaudRate}";
    public bool IsOpen => _port?.IsOpen == true;

    public void Open()
    {
        if (IsOpen) return;

        if (!PortExists(_options.PortName))
            throw new TransportException($"{_options.PortName} nao existe. A placa esta conectada? O driver da ponte USB-UART esta instalado?");

        var port = new SerialPort(_options.PortName, _options.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = _options.DtrEnable,
            RtsEnable = _options.RtsEnable,
            ReadTimeout = 50,
            WriteTimeout = _options.WriteTimeoutMs,
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 16 * 1024,
        };

        try
        {
            port.Open();
            port.DiscardInBuffer();
        }
        catch (UnauthorizedAccessException ex)
        {
            port.Dispose();
            throw new TransportException(
                $"{_options.PortName} esta em uso por outro programa (monitor serial do PlatformIO/Arduino/VS Code?). Feche-o e tente de novo.", ex);
        }
        catch (FileNotFoundException ex)
        {
            port.Dispose();
            throw new TransportException($"{_options.PortName} nao existe. A placa esta conectada?", ex);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            port.Dispose();
            throw new TransportException($"Nao foi possivel abrir {_options.PortName}: {ex.Message}", ex);
        }

        _port = port;
    }

    private static bool PortExists(string name)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return File.Exists(name);
            return SerialPort.GetPortNames().Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;   // na duvida, deixa o Open dizer o que houve
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        SerialPort port = _port ?? throw new TransportException("Porta fechada.");
        try
        {
            if (!port.IsOpen) throw new TransportException($"{_options.PortName} foi desconectada.");

            int timeout = Math.Max(1, timeoutMs);
            if (port.ReadTimeout != timeout) port.ReadTimeout = timeout;
            return port.BaseStream.Read(buffer);
        }
        catch (TimeoutException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
        {
            throw new TransportException($"Erro de leitura em {_options.PortName}: {ex.Message}", ex);
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        SerialPort port = _port ?? throw new TransportException("Porta fechada.");
        try
        {
            port.BaseStream.Write(data);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException or TimeoutException)
        {
            throw new TransportException($"Erro de escrita em {_options.PortName}: {ex.Message}", ex);
        }
    }

    public void DiscardInput()
    {
        try { _port?.DiscardInBuffer(); }
        catch (Exception) { /* porta ja caiu: nada a descartar */ }
    }

    public void Dispose()
    {
        SerialPort? port = Interlocked.Exchange(ref _port, null);
        if (port is null) return;
        try { port.Close(); }
        catch (Exception) { /* fechar uma porta que sumiu pode lancar; nao importa */ }
        port.Dispose();
    }
}
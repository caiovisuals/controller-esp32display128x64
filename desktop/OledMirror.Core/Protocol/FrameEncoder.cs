using OledMirror.Core.Imaging;

namespace OledMirror.Core.Protocol;

/// <summary>Estrategia de codificacao de frames.</summary>
public enum FrameEncoding
{
    Raw,
    Rle,
    Delta,
    DeltaRle,
    /// <summary>Calcula os candidatos que o dispositivo aceita e envia o menor.</summary>
    Auto,
}

/// <summary>Contadores do codificador.</summary>
public sealed class EncoderStatistics
{
    private long _framesRaw, _framesRle, _framesDelta, _framesDeltaRle, _skipped, _payloadBytes, _rawEquivalent;

    public long FramesRaw => Interlocked.Read(ref _framesRaw);
    public long FramesRle => Interlocked.Read(ref _framesRle);
    public long FramesDelta => Interlocked.Read(ref _framesDelta);
    public long FramesDeltaRle => Interlocked.Read(ref _framesDeltaRle);
    public long FramesSkippedUnchanged => Interlocked.Read(ref _skipped);

    /// <summary>Soma dos payloads produzidos.</summary>
    public long PayloadBytes => Interlocked.Read(ref _payloadBytes);

    /// <summary>Quanto teria custado mandar os mesmos frames crus.</summary>
    public long RawEquivalentBytes => Interlocked.Read(ref _rawEquivalent);

    /// <summary>Payload produzido / equivalente cru (1 = nenhuma economia).</summary>
    public double CompressionRatio => RawEquivalentBytes == 0 ? 1.0 : (double)PayloadBytes / RawEquivalentBytes;

    internal void Count(CommandId command, int payloadBytes)
    {
        switch (command)
        {
            case CommandId.FrameRaw: Interlocked.Increment(ref _framesRaw); break;
            case CommandId.FrameRle: Interlocked.Increment(ref _framesRle); break;
            case CommandId.FrameDelta: Interlocked.Increment(ref _framesDelta); break;
            default: Interlocked.Increment(ref _framesDeltaRle); break;
        }
        Interlocked.Add(ref _payloadBytes, payloadBytes);
        Interlocked.Add(ref _rawEquivalent, DisplayGeometry.FrameBytes);
    }

    internal void CountSkipped() => Interlocked.Increment(ref _skipped);

    /// <summary>Desfaz a contagem de um frame que nao chegou a ser enviado.</summary>
    internal void Uncount(CommandId command, int payloadBytes)
    {
        switch (command)
        {
            case CommandId.FrameRaw: Interlocked.Decrement(ref _framesRaw); break;
            case CommandId.FrameRle: Interlocked.Decrement(ref _framesRle); break;
            case CommandId.FrameDelta: Interlocked.Decrement(ref _framesDelta); break;
            default: Interlocked.Decrement(ref _framesDeltaRle); break;
        }
        Interlocked.Add(ref _payloadBytes, -payloadBytes);
        Interlocked.Add(ref _rawEquivalent, -DisplayGeometry.FrameBytes);
    }

    public EncoderStatistics Snapshot()
    {
        var s = new EncoderStatistics();
        s._framesRaw = FramesRaw;
        s._framesRle = FramesRle;
        s._framesDelta = FramesDelta;
        s._framesDeltaRle = FramesDeltaRle;
        s._skipped = FramesSkippedUnchanged;
        s._payloadBytes = PayloadBytes;
        s._rawEquivalent = RawEquivalentBytes;
        return s;
    }
}

/// <summary>
/// Escolhe como cada frame vai para o fio. Guarda o ultimo frame enviado, que e'
/// o que o host acredita estar no painel, e gera deltas sobre ele.
///
/// Nao aloca por frame: todos os buffers sao pre-alocados e o payload devolvido
/// aponta para um deles, valido ate a proxima chamada.
/// </summary>
public sealed class FrameEncoder
{
    private const int Width = DisplayGeometry.Width;
    private const int FrameBytes = DisplayGeometry.FrameBytes;
    private const int DeltaHeader = 4;

    private readonly byte[] _previous = new byte[FrameBytes];
    private readonly byte[] _backup = new byte[FrameBytes];
    private readonly byte[] _rle = new byte[Rle.MaxEncodedSize(FrameBytes)];
    private readonly byte[] _delta = new byte[DeltaHeader + FrameBytes];
    private readonly byte[] _deltaRle = new byte[DeltaHeader + Rle.MaxEncodedSize(FrameBytes)];
    private readonly byte[] _raw = new byte[FrameBytes];

    private bool _hasPrevious;
    private bool _backupHadPrevious;
    private bool _canRollBack;
    private CommandId _lastCommand;
    private int _lastLength;

    public FrameEncoder(DeviceCapabilities capabilities = DeviceCapabilities.All)
    {
        Capabilities = capabilities;
    }

    /// <summary>O que o dispositivo anunciou. So sao usadas codificacoes suportadas.</summary>
    public DeviceCapabilities Capabilities { get; set; }

    public FrameEncoding Preference { get; set; } = FrameEncoding.Auto;

    /// <summary>Frame identico ao anterior nao e' enviado.</summary>
    public bool SkipUnchangedFrames { get; set; } = true;

    public EncoderStatistics Statistics { get; } = new();

    /// <summary>
    /// Esquece o conteudo do painel: o proximo frame vai completo. Use depois de
    /// reconectar ou quando um frame se perdeu.
    /// </summary>
    public void Invalidate()
    {
        _hasPrevious = false;
        _canRollBack = false;
    }

    /// <summary>
    /// Desfaz o ultimo <see cref="TryEncode"/>: o frame nao chegou a ir para o fio
    /// (por exemplo, janela de controle de fluxo cheia), entao o painel continua
    /// com o conteudo anterior.
    /// </summary>
    public void RollBack()
    {
        if (!_canRollBack) return;
        Buffer.BlockCopy(_backup, 0, _previous, 0, FrameBytes);
        _hasPrevious = _backupHadPrevious;
        _canRollBack = false;
        Statistics.Uncount(_lastCommand, _lastLength);
    }

    /// <summary>
    /// Codifica um frame de 1024 bytes. Devolve false quando nao ha nada a enviar
    /// (frame identico ao anterior).
    /// </summary>
    public bool TryEncode(ReadOnlySpan<byte> frame, out CommandId command, out ReadOnlyMemory<byte> payload)
    {
        if (frame.Length != FrameBytes)
            throw new ArgumentException($"Frame precisa ter {FrameBytes} bytes.", nameof(frame));

        _canRollBack = false;

        bool hasRect = false;
        int x0 = 0, x1 = 0, p0 = 0, p1 = 0;
        if (_hasPrevious)
        {
            hasRect = ChangedRect(frame, _previous, out x0, out x1, out p0, out p1);
            if (!hasRect && SkipUnchangedFrames)
            {
                Statistics.CountSkipped();
                command = default;
                payload = default;
                return false;
            }
        }

        bool rleOk = (Capabilities & DeviceCapabilities.Rle) != 0;
        bool deltaOk = (Capabilities & DeviceCapabilities.Delta) != 0 && _hasPrevious;
        if (deltaOk && !hasRect)
        {
            // Frame identico enviado de proposito: um delta minimo de 1 byte basta.
            x0 = x1 = p0 = p1 = 0;
        }

        switch (Preference)
        {
            case FrameEncoding.Rle when rleOk:
                Select(CommandId.FrameRle, EncodeRle(frame), out command, out payload);
                break;
            case FrameEncoding.Delta when deltaOk:
                Select(CommandId.FrameDelta, EncodeDelta(frame, x0, x1, p0, p1), out command, out payload);
                break;
            case FrameEncoding.DeltaRle when deltaOk && rleOk:
            {
                int n = EncodeDeltaRle(frame, x0, x1, p0, p1);
                if (n > 0) Select(CommandId.FrameDeltaRle, n, out command, out payload);
                else Select(CommandId.FrameDelta, EncodeDelta(frame, x0, x1, p0, p1), out command, out payload);
                break;
            }
            case FrameEncoding.Auto:
            {
                CommandId best = CommandId.FrameRaw;
                int bestLength = FrameBytes;

                if (deltaOk)
                {
                    int n = EncodeDelta(frame, x0, x1, p0, p1);
                    if (n < bestLength) { best = CommandId.FrameDelta; bestLength = n; }

                    if (rleOk)
                    {
                        n = EncodeDeltaRle(frame, x0, x1, p0, p1);
                        if (n > 0 && n < bestLength) { best = CommandId.FrameDeltaRle; bestLength = n; }
                    }
                }

                // RLE do frame inteiro so pode ganhar de um delta se o delta for grande.
                if (rleOk && bestLength > 64)
                {
                    int n = EncodeRle(frame);
                    if (n > 0 && n < bestLength) { best = CommandId.FrameRle; bestLength = n; }
                }

                if (best == CommandId.FrameRaw) frame.CopyTo(_raw);
                Select(best, bestLength, out command, out payload);
                break;
            }
            default:
                frame.CopyTo(_raw);
                Select(CommandId.FrameRaw, FrameBytes, out command, out payload);
                break;
        }

        // O frame passa a ser a referencia; guarda a anterior para um eventual RollBack.
        Buffer.BlockCopy(_previous, 0, _backup, 0, FrameBytes);
        _backupHadPrevious = _hasPrevious;
        frame.CopyTo(_previous);
        _hasPrevious = true;
        _canRollBack = true;

        _lastCommand = command;
        _lastLength = payload.Length;
        Statistics.Count(command, payload.Length);
        return true;
    }

    private void Select(CommandId command, int length, out CommandId selected, out ReadOnlyMemory<byte> payload)
    {
        selected = command;
        payload = command switch
        {
            CommandId.FrameRle => _rle.AsMemory(0, length),
            CommandId.FrameDelta => _delta.AsMemory(0, length),
            CommandId.FrameDeltaRle => _deltaRle.AsMemory(0, length),
            _ => _raw.AsMemory(0, FrameBytes),
        };
    }

    private int EncodeRle(ReadOnlySpan<byte> frame) => Rle.Encode(frame, _rle);

    private int EncodeDelta(ReadOnlySpan<byte> frame, int x0, int x1, int p0, int p1)
    {
        WriteRect(_delta, x0, x1, p0, p1);
        int columns = x1 - x0 + 1;
        int o = DeltaHeader;
        for (int p = p0; p <= p1; p++)
        {
            frame.Slice(p * Width + x0, columns).CopyTo(_delta.AsSpan(o));
            o += columns;
        }
        return o;
    }

    private int EncodeDeltaRle(ReadOnlySpan<byte> frame, int x0, int x1, int p0, int p1)
    {
        int regionLength = EncodeDelta(frame, x0, x1, p0, p1) - DeltaHeader;
        WriteRect(_deltaRle, x0, x1, p0, p1);
        int n = Rle.Encode(_delta.AsSpan(DeltaHeader, regionLength), _deltaRle.AsSpan(DeltaHeader));
        return n < 0 ? -1 : DeltaHeader + n;
    }

    private static void WriteRect(byte[] buffer, int x0, int x1, int p0, int p1)
    {
        buffer[0] = (byte)x0;
        buffer[1] = (byte)x1;
        buffer[2] = (byte)p0;
        buffer[3] = (byte)p1;
    }

    /// <summary>Menor retangulo (colunas x paginas) que contem todos os bytes alterados.</summary>
    private static bool ChangedRect(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous,
                                    out int x0, out int x1, out int p0, out int p1)
    {
        x0 = Width; x1 = -1; p0 = DisplayGeometry.Pages; p1 = -1;

        for (int p = 0; p < DisplayGeometry.Pages; p++)
        {
            ReadOnlySpan<byte> a = current.Slice(p * Width, Width);
            ReadOnlySpan<byte> b = previous.Slice(p * Width, Width);

            int first = a.CommonPrefixLength(b);
            if (first == Width) continue;

            int last = Width - 1;
            while (a[last] == b[last]) last--;

            if (p < p0) p0 = p;
            p1 = p;
            if (first < x0) x0 = first;
            if (last > x1) x1 = last;
        }

        return p1 >= 0;
    }
}
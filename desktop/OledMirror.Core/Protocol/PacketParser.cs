using System.Buffers.Binary;

namespace OledMirror.Core.Protocol;

/// <summary>Pacote validado (os dois CRCs conferiram).</summary>
public sealed class Packet
{
    public Packet(CommandId command, byte sequence, byte[] payload)
    {
        Command = command;
        Sequence = sequence;
        Payload = payload;
    }

    public CommandId Command { get; }
    public byte Sequence { get; }
    public byte[] Payload { get; }

    public override string ToString() => $"{Command} seq={Sequence} len={Payload.Length}";
}

/// <summary>Contadores do decodificador.</summary>
public sealed class ParserStatistics
{
    public long PacketsDecoded { get; internal set; }
    public long HeaderCrcErrors { get; internal set; }
    public long PayloadCrcErrors { get; internal set; }
    public long LengthErrors { get; internal set; }
    public long VersionErrors { get; internal set; }
    public long DiscardedBytes { get; internal set; }
    public long Resyncs { get; internal set; }

    public ParserStatistics Snapshot() => (ParserStatistics)MemberwiseClone();

    public override string ToString()
        => $"{PacketsDecoded} pacotes ok, CRC cabecalho {HeaderCrcErrors}, CRC payload {PayloadCrcErrors}, " +
           $"tamanho {LengthErrors}, versao {VersionErrors}, {DiscardedBytes} bytes descartados, {Resyncs} resyncs";
}

/// <summary>
/// Decodificador incremental, com a mesma maquina de estados do firmware
/// (firmware/src/protocol/parser.cpp): aceita bytes em qualquer fragmentacao,
/// descarta lixo e nunca trava num LENGTH corrompido.
/// </summary>
public sealed class PacketParser
{
    private readonly byte[] _buffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly Queue<Packet> _ready = new();
    private int _length;
    private bool _wasAligned = true;

    public ParserStatistics Statistics { get; } = new();

    /// <summary>Quantos pacotes estao prontos para <see cref="TryDequeue"/>.</summary>
    public int PendingCount => _ready.Count;

    /// <summary>Alimenta o parser. Devolve quantos pacotes completos foram decodificados nesta chamada.</summary>
    public int Push(ReadOnlySpan<byte> data)
    {
        int decoded = 0;
        while (!data.IsEmpty)
        {
            if (_length == _buffer.Length) Discard(_length);

            int n = Math.Min(data.Length, _buffer.Length - _length);
            data[..n].CopyTo(_buffer.AsSpan(_length));
            _length += n;
            data = data[n..];

            decoded += TryParse();
        }
        return decoded;
    }

    public bool TryDequeue(out Packet packet)
    {
        if (_ready.Count > 0)
        {
            packet = _ready.Dequeue();
            return true;
        }
        packet = null!;
        return false;
    }

    /// <summary>Esquece qualquer pacote parcial. Pacotes ja prontos tambem sao descartados.</summary>
    public void Reset()
    {
        _length = 0;
        _wasAligned = true;
        _ready.Clear();
    }

    private int TryParse()
    {
        int decoded = 0;
        for (;;)
        {
            if (_length < 2) return decoded;

            // 1. Alinhar na assinatura.
            if (_buffer[0] != ProtocolConstants.Sof0 || _buffer[1] != ProtocolConstants.Sof1)
            {
                int scan = 1;
                while (scan < _length && _buffer[scan] != ProtocolConstants.Sof0) scan++;
                Discard(scan);
                continue;
            }

            if (_length < ProtocolConstants.HeaderSize) return decoded;

            // 2. O cabecalho tem que estar integro antes de acreditarmos no LENGTH.
            if (Crc.Crc8(_buffer.AsSpan(0, 7)) != _buffer[7])
            {
                Statistics.HeaderCrcErrors++;
                Discard(1);
                continue;
            }

            // 3. Versao.
            if (_buffer[2] != ProtocolConstants.Version)
            {
                Statistics.VersionErrors++;
                Discard(2);
                continue;
            }

            // 4. Tamanho.
            int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(4, 2));
            if (payloadLength > ProtocolConstants.MaxPayloadSize)
            {
                Statistics.LengthErrors++;
                Discard(2);
                continue;
            }

            int total = ProtocolConstants.OverheadSize + payloadLength;
            if (_length < total) return decoded;

            // 5. Payload. Falhou: descarta so a assinatura, o inicio real pode estar logo adiante.
            ReadOnlySpan<byte> payload = _buffer.AsSpan(ProtocolConstants.HeaderSize, payloadLength);
            ushort expected = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(ProtocolConstants.HeaderSize + payloadLength, 2));
            if (Crc.Crc16(payload) != expected)
            {
                Statistics.PayloadCrcErrors++;
                Discard(2);
                continue;
            }

            _ready.Enqueue(new Packet((CommandId)_buffer[3], _buffer[6], payload.ToArray()));
            Statistics.PacketsDecoded++;
            decoded++;
            _wasAligned = true;

            int remaining = _length - total;
            if (remaining > 0) Buffer.BlockCopy(_buffer, total, _buffer, 0, remaining);
            _length = remaining;
        }
    }

    private void Discard(int count)
    {
        if (count <= 0) return;
        if (count >= _length)
        {
            Statistics.DiscardedBytes += _length;
            _length = 0;
        }
        else
        {
            Buffer.BlockCopy(_buffer, count, _buffer, 0, _length - count);
            _length -= count;
            Statistics.DiscardedBytes += count;
        }

        if (_wasAligned)
        {
            Statistics.Resyncs++;
            _wasAligned = false;
        }
    }
}
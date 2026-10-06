using System.Buffers.Binary;

namespace OledMirror.Core.Protocol;

/// <summary>Monta pacotes no formato do fio. Equivalente ao EncodePacket do firmware.</summary>
public static class PacketEncoder
{
    /// <summary>Tamanho total no fio de um pacote com este payload.</summary>
    public static int PacketSize(int payloadLength) => ProtocolConstants.OverheadSize + payloadLength;

    /// <summary>Monta o pacote num array novo. Conveniente fora do caminho quente.</summary>
    public static byte[] Encode(CommandId command, byte sequence, ReadOnlySpan<byte> payload = default)
    {
        CheckLength(payload.Length);
        var packet = new byte[PacketSize(payload.Length)];
        Encode(packet, command, sequence, payload);
        return packet;
    }

    /// <summary>Monta o pacote em <paramref name="destination"/> sem alocar. Devolve o tamanho escrito.</summary>
    public static int Encode(Span<byte> destination, CommandId command, byte sequence, ReadOnlySpan<byte> payload)
    {
        CheckLength(payload.Length);
        int total = PacketSize(payload.Length);
        if (destination.Length < total)
            throw new ArgumentException($"Destino de {destination.Length} bytes nao comporta um pacote de {total}.", nameof(destination));

        destination[0] = ProtocolConstants.Sof0;
        destination[1] = ProtocolConstants.Sof1;
        destination[2] = ProtocolConstants.Version;
        destination[3] = (byte)command;
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(4, 2), (ushort)payload.Length);
        destination[6] = sequence;
        destination[7] = Crc.Crc8(destination[..7]);

        payload.CopyTo(destination[ProtocolConstants.HeaderSize..]);

        ushort crc = Crc.Crc16(payload);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(ProtocolConstants.HeaderSize + payload.Length, 2), crc);
        return total;
    }

    private static void CheckLength(int length)
    {
        if (length > ProtocolConstants.MaxPayloadSize)
            throw new ArgumentOutOfRangeException(nameof(length), length,
                $"Payload acima do maximo de {ProtocolConstants.MaxPayloadSize} bytes.");
    }
}
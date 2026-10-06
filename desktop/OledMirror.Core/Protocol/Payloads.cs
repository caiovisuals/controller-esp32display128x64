using System.Buffers.Binary;
using System.Text;

namespace OledMirror.Core.Protocol;

/// <summary>Montagem dos payloads de controle (host -> dispositivo).</summary>
public static class Payloads
{
    /// <summary>Capacidades que o host declara no HELLO: RLE, delta e texto.</summary>
    public const DeviceCapabilities HostCapabilities = DeviceCapabilities.Rle | DeviceCapabilities.Delta | DeviceCapabilities.Text;

    /// <summary>HELLO: [versao][caps_lo][caps_hi][reservado].</summary>
    public static byte[] BuildHello()
    {
        var payload = new byte[4];
        payload[0] = ProtocolConstants.Version;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1, 2), (ushort)HostCapabilities);
        return payload;
    }

    public static byte[] BuildPing(uint token)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, token);
        return payload;
    }

    /// <summary>
    /// TEXT em UTF-8. O firmware desenha ate 60 caracteres ASCII; o texto e'
    /// cortado aqui para nunca exceder o payload maximo.
    /// </summary>
    public static byte[] BuildText(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        return bytes.Length <= ProtocolConstants.MaxPayloadSize ? bytes : bytes[..ProtocolConstants.MaxPayloadSize];
    }

    /// <summary>Uma entrada TLV de 1 byte.</summary>
    public static byte[] BuildConfigByte(ConfigKey key, byte value) => BuildConfig((key, new[] { value }));

    /// <summary>Uma entrada TLV de 2 bytes (uint16 little-endian).</summary>
    public static byte[] BuildConfigUInt16(ConfigKey key, ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return BuildConfig((key, bytes));
    }

    /// <summary>Sequencia de entradas [chave][tamanho][valor...].</summary>
    public static byte[] BuildConfig(params (ConfigKey Key, byte[] Value)[] entries)
    {
        int length = 0;
        foreach ((ConfigKey _, byte[] value) in entries)
        {
            if (value.Length > 255) throw new ArgumentException("Valor de configuracao acima de 255 bytes.");
            length += 2 + value.Length;
        }

        var payload = new byte[length];
        int o = 0;
        foreach ((ConfigKey key, byte[] value) in entries)
        {
            payload[o++] = (byte)key;
            payload[o++] = (byte)value.Length;
            value.CopyTo(payload, o);
            o += value.Length;
        }
        return payload;
    }
}
using System.Runtime.CompilerServices;

namespace OledMirror.Core.Protocol;

/// <summary>
/// CRC-8/ATM (poly 0x07, init 0x00) para o cabecalho e CRC-16/CCITT-FALSE
/// (poly 0x1021, init 0xFFFF) para o payload. Mesmas tabelas do firmware.
/// </summary>
public static class Crc
{
    private static readonly byte[] Table8 = BuildTable8();
    private static readonly ushort[] Table16 = BuildTable16();

    // Otimizado desde a primeira chamada: roda sobre cada byte de cada pacote.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static byte Crc8(ReadOnlySpan<byte> data, byte seed = 0x00)
    {
        byte crc = seed;
        foreach (byte b in data) crc = Table8[crc ^ b];
        return crc;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ushort Crc16(ReadOnlySpan<byte> data, ushort seed = 0xFFFF)
    {
        ushort crc = seed;
        foreach (byte b in data) crc = (ushort)((crc << 8) ^ Table16[((crc >> 8) ^ b) & 0xFF]);
        return crc;
    }

    private static byte[] BuildTable8()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            byte c = (byte)i;
            for (int b = 0; b < 8; b++) c = (c & 0x80) != 0 ? (byte)((c << 1) ^ 0x07) : (byte)(c << 1);
            table[i] = c;
        }
        return table;
    }

    private static ushort[] BuildTable16()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort c = (ushort)(i << 8);
            for (int b = 0; b < 8; b++) c = (c & 0x8000) != 0 ? (ushort)((c << 1) ^ 0x1021) : (ushort)(c << 1);
            table[i] = c;
        }
        return table;
    }
}
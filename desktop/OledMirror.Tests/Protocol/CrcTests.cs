using OledMirror.Core.Protocol;

namespace OledMirror.Tests.Protocol;

/// <summary>
/// Vetores conhecidos. O firmware tem os mesmos valores em
/// firmware/test/test_protocol/test_crc.cpp: se as duas pontas divergirem,
/// nenhum pacote passa e este teste diz de que lado esta o erro.
/// </summary>
public class CrcTests {
    private static readonly byte[] Check = "123456789"u8.ToArray();

    [Fact]
    public void Crc8_MatchesKnownCheckValue()
    {
        // CRC-8/ATM ("SMBUS"), poly 0x07 init 0x00 -> check = 0xF4
        Assert.Equal(0xF4, Crc.Crc8(Check));
    }

    [Fact]
    public void Crc16_MatchesKnownCheckValue()
    {
        // CRC-16/CCITT-FALSE, poly 0x1021 init 0xFFFF -> check = 0x29B1
        Assert.Equal(0x29B1, Crc.Crc16(Check));
    }

    [Fact]
    public void Crc8_OfEmptyInput_IsSeed()
    {
        Assert.Equal(0x00, Crc.Crc8(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Crc16_OfEmptyInput_IsSeed()
    {
        Assert.Equal(0xFFFF, Crc.Crc16(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Crc16_DetectsSingleBitFlip()
    {
        var data = new byte[1024];
        new Random(7).NextBytes(data);
        ushort original = Crc.Crc16(data);

        for (int i = 0; i < data.Length; i += 37)
        {
            data[i] ^= 0x01;
            Assert.NotEqual(original, Crc.Crc16(data));
            data[i] ^= 0x01;
        }
    }
}
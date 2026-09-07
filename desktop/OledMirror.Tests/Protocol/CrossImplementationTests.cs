using OledMirror.Core.Imaging;
using OledMirror.Core.Protocol;

namespace OledMirror.Tests.Protocol;

/// <summary>
/// Vetores byte a byte gerados pela implementacao C++ do firmware
/// (firmware/src/protocol/parser.cpp). Se um dos lados mudar o layout do
/// cabecalho, a ordem dos bytes ou o polinomio do CRC, estes testes falham
/// imediatamente - em vez de o projeto so "nao funcionar" com o hardware na mao,
/// que e' o pior momento possivel para descobrir.
///
/// Para regerar os vetores, veja docs/PROTOCOL.md, secao
/// "Verificacao cruzada entre as implementacoes".
/// </summary>
public class CrossImplementationTests {
    [Fact]
    public void EmptyPingPacket_MatchesTheFirmwareBytes()
    {
        byte[] packet = PacketEncoder.Encode(CommandId.Ping, 0);
        Assert.Equal("AA55010200000053FFFF", Convert.ToHexString(packet));
    }

    [Fact]
    public void HelloPacket_MatchesTheFirmwareBytes()
    {
        byte[] packet = PacketEncoder.Encode(CommandId.Hello, 7, new byte[] { 0x01, 0x07, 0x00, 0x00 });
        Assert.Equal("AA550101040007D701070000E477", Convert.ToHexString(packet));
    }

    [Fact]
    public void SixteenBytePayload_MatchesTheFirmwareBytes()
    {
        var payload = new byte[16];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 1);

        byte[] packet = PacketEncoder.Encode(CommandId.FrameDelta, 200, payload);
        Assert.Equal("AA5501121000C8E001203F5E7D9CBBDAF91837567594B3D27B72", Convert.ToHexString(packet));
    }

    [Fact]
    public void FullRawFrame_MatchesTheFirmwareHeaderAndCrc()
    {
        var frame = new byte[DisplayGeometry.FrameBytes];
        for (int i = 0; i < frame.Length; i++) frame[i] = (byte)(i * 7 + 3);

        byte[] packet = PacketEncoder.Encode(CommandId.FrameRaw, 42, frame);

        Assert.Equal(1034, packet.Length);
        // Cabecalho: SOF0 SOF1 VER CMD LEN_LO LEN_HI SEQ CRC8
        Assert.Equal("AA55011000042A9A", Convert.ToHexString(packet.AsSpan(0, 8)));
        // CRC16 do payload, little-endian, no fim do pacote.
        Assert.Equal("8DCC", Convert.ToHexString(packet.AsSpan(packet.Length - 2, 2)));
    }
}
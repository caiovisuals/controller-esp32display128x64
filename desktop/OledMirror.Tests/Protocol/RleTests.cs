using OledMirror.Core.Imaging;
using OledMirror.Core.Protocol;

namespace OledMirror.Tests.Protocol;

public class RleTests {
    private static void RoundTrip(byte[] input)
    {
        var encoded = new byte[Rle.MaxEncodedSize(input.Length)];
        int n = Rle.Encode(input, encoded);
        Assert.True(n > 0, "Codificacao falhou.");

        var decoded = new byte[input.Length];
        int m = Rle.Decode(encoded.AsSpan(0, n), decoded);

        Assert.Equal(input.Length, m);
        Assert.Equal(input, decoded);
    }

    [Fact]
    public void AllZeros_RoundTripsAndCompressesHard()
    {
        var input = new byte[DisplayGeometry.FrameBytes];
        RoundTrip(input);

        var encoded = new byte[Rle.MaxEncodedSize(input.Length)];
        int n = Rle.Encode(input, encoded);
        // 1024 zeros = 8 corridas de 128... na verdade corridas de ate 129:
        // deve caber em menos de 20 bytes.
        Assert.True(n < 20, $"Esperava forte compressao, obteve {n} bytes.");
    }

    [Fact]
    public void AllOnes_RoundTrips() => RoundTrip(Enumerable.Repeat((byte)0xFF, DisplayGeometry.FrameBytes).ToArray());

    [Fact]
    public void Alternating_RoundTrips()
    {
        var input = new byte[DisplayGeometry.FrameBytes];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)(i % 2 == 0 ? 0xAA : 0x55);
        RoundTrip(input);
    }

    [Fact]
    public void RandomData_RoundTrips_AndStaysWithinWorstCase()
    {
        var input = new byte[DisplayGeometry.FrameBytes];
        new Random(11).NextBytes(input);
        RoundTrip(input);

        var encoded = new byte[Rle.MaxEncodedSize(input.Length)];
        int n = Rle.Encode(input, encoded);
        Assert.True(n <= Rle.MaxEncodedSize(input.Length));
    }

    [Fact]
    public void WorstCaseSize_FitsInMaxPayload()
    {
        // Garantia estrutural: o pior caso do RLE nunca estoura o buffer que o
        // firmware reserva.
        Assert.True(Rle.MaxEncodedSize(DisplayGeometry.FrameBytes) <= ProtocolConstants.MaxPayloadSize);
        Assert.Equal(1032, Rle.MaxEncodedSize(DisplayGeometry.FrameBytes));
    }

    [Fact]
    public void EmptyInput_ProducesEmptyOutput()
    {
        var encoded = new byte[16];
        Assert.Equal(0, Rle.Encode(ReadOnlySpan<byte>.Empty, encoded));
        Assert.Equal(0, Rle.Decode(ReadOnlySpan<byte>.Empty, new byte[16]));
    }

    [Fact]
    public void Encode_ReturnsMinusOne_WhenDestinationTooSmall()
    {
        var input = new byte[512];
        new Random(3).NextBytes(input);
        Assert.Equal(-1, Rle.Encode(input, new byte[8]));
    }

    [Theory]
    [InlineData(new byte[] { 0x05 })]                       // literal anunciado, sem dados
    [InlineData(new byte[] { 0x7F, 0x01, 0x02 })]           // literal de 128 truncado
    [InlineData(new byte[] { 0x80 })]                       // repeticao sem o byte de valor
    public void TruncatedStream_IsRejected(byte[] malformed)
    {
        Assert.Equal(-1, Rle.Decode(malformed, new byte[1024]));
    }

    [Fact]
    public void StreamThatWouldOverflowDestination_IsRejected()
    {
        // Uma repeticao de 129 bytes contra um destino de 8: o decodificador tem
        // que recusar em vez de escrever fora. Este e' o caminho que roda no ESP32
        // sobre bytes vindos da serial.
        byte[] hostile = { 0xFF, 0x41 };
        Assert.Equal(-1, Rle.Decode(hostile, new byte[8]));
    }

    [Fact]
    public void LongRepeatRun_IsSplitCorrectly()
    {
        // 300 bytes iguais precisam virar 3 corridas (129 + 129 + 42).
        var input = Enumerable.Repeat((byte)0x7E, 300).ToArray();
        RoundTrip(input);
    }
}
using OledMirror.Core.Imaging;
using OledMirror.Core.Protocol;

namespace OledMirror.Tests.Protocol;

public class FrameCodecTests {
    private static byte[] RandomFrame(int seed)
    {
        var f = new byte[DisplayGeometry.FrameBytes];
        new Random(seed).NextBytes(f);
        return f;
    }

    [Fact]
    public void FirstFrame_IsAlwaysSentWhole()
    {
        var encoder = new FrameEncoder();
        byte[] frame = RandomFrame(1);

        Assert.True(encoder.TryEncode(frame, out CommandId cmd, out ReadOnlyMemory<byte> payload));
        // Sem frame anterior nao existe delta; com ruido puro o RLE nao ajuda.
        Assert.Equal(CommandId.FrameRaw, cmd);
        Assert.Equal(DisplayGeometry.FrameBytes, payload.Length);
    }

    [Fact]
    public void UnchangedFrame_IsSkipped()
    {
        var encoder = new FrameEncoder();
        byte[] frame = RandomFrame(2);

        Assert.True(encoder.TryEncode(frame, out _, out _));
        Assert.False(encoder.TryEncode(frame, out _, out _));
        Assert.Equal(1, encoder.Statistics.FramesSkippedUnchanged);
    }

    [Fact]
    public void SmallChange_UsesDelta_AndShrinksThePayloadDramatically()
    {
        var encoder = new FrameEncoder();
        var frame = new byte[DisplayGeometry.FrameBytes];
        Assert.True(encoder.TryEncode(frame, out _, out _));

        frame[3 * DisplayGeometry.Width + 40] = 0xFF;          // um unico byte muda

        Assert.True(encoder.TryEncode(frame, out CommandId cmd, out ReadOnlyMemory<byte> payload));
        Assert.True(cmd is CommandId.FrameDelta or CommandId.FrameDeltaRle);
        Assert.True(payload.Length < 20, $"Delta de 1 byte gerou {payload.Length} bytes.");
    }

    [Fact]
    public void BlankFrame_UsesRle()
    {
        var encoder = new FrameEncoder();
        var frame = new byte[DisplayGeometry.FrameBytes];

        Assert.True(encoder.TryEncode(frame, out CommandId cmd, out ReadOnlyMemory<byte> payload));
        Assert.Equal(CommandId.FrameRle, cmd);
        Assert.True(payload.Length < 32);
    }

    [Fact]
    public void EveryEncodingRoundTripsThroughTheDecoder()
    {
        var encoder = new FrameEncoder();
        var decoder = new FrameDecoder();
        var deviceFrame = new byte[DisplayGeometry.FrameBytes];

        // Sequencia variada: ruido, quase-igual, em branco, ruido de novo.
        byte[][] frames =
        {
            RandomFrame(10),
            Mutate(RandomFrame(10), 5),
            new byte[DisplayGeometry.FrameBytes],
            RandomFrame(11),
            Mutate(RandomFrame(11), 1),
        };

        foreach (byte[] frame in frames)
        {
            if (!encoder.TryEncode(frame, out CommandId cmd, out ReadOnlyMemory<byte> payload)) continue;
            Assert.Equal(FrameDecodeResult.Ok, decoder.Apply(cmd, payload.Span, deviceFrame));
            Assert.Equal(frame, deviceFrame);   // o painel espelha exatamente o host
        }
    }

    [Theory]
    [InlineData(FrameEncoding.Raw)]
    [InlineData(FrameEncoding.Rle)]
    [InlineData(FrameEncoding.Delta)]
    [InlineData(FrameEncoding.DeltaRle)]
    [InlineData(FrameEncoding.Auto)]
    public void ForcedEncoding_AlwaysProducesADecodableFrame(FrameEncoding preference)
    {
        var encoder = new FrameEncoder { Preference = preference, SkipUnchangedFrames = false };
        var decoder = new FrameDecoder();
        var deviceFrame = new byte[DisplayGeometry.FrameBytes];

        for (int i = 0; i < 6; i++)
        {
            byte[] frame = Mutate(RandomFrame(20), i * 7);
            Assert.True(encoder.TryEncode(frame, out CommandId cmd, out ReadOnlyMemory<byte> payload));
            Assert.Equal(FrameDecodeResult.Ok, decoder.Apply(cmd, payload.Span, deviceFrame));
            Assert.Equal(frame, deviceFrame);
        }
    }

    [Fact]
    public void Invalidate_ForcesAFullFrame()
    {
        var encoder = new FrameEncoder();
        byte[] frame = RandomFrame(30);

        Assert.True(encoder.TryEncode(frame, out _, out _));
        encoder.Invalidate();   // e' o que acontece depois de reconectar

        Assert.True(encoder.TryEncode(frame, out CommandId cmd, out ReadOnlyMemory<byte> payload));
        Assert.Equal(CommandId.FrameRaw, cmd);
        Assert.Equal(DisplayGeometry.FrameBytes, payload.Length);
    }

    [Fact]
    public void DeviceWithoutCapabilities_OnlyGetsRawFrames()
    {
        var encoder = new FrameEncoder(DeviceCapabilities.None) { SkipUnchangedFrames = false };
        var frame = new byte[DisplayGeometry.FrameBytes];

        for (int i = 0; i < 3; i++)
        {
            frame[i] = (byte)i;
            Assert.True(encoder.TryEncode(frame, out CommandId cmd, out _));
            Assert.Equal(CommandId.FrameRaw, cmd);
        }
    }

    // ---- validacao defensiva do lado do dispositivo ----

    [Fact]
    public void RawFrameWithWrongLength_IsRejected()
    {
        var decoder = new FrameDecoder();
        var fb = new byte[DisplayGeometry.FrameBytes];
        Assert.Equal(FrameDecodeResult.BadLength, decoder.Apply(CommandId.FrameRaw, new byte[1023], fb));
        Assert.Equal(FrameDecodeResult.BadLength, decoder.Apply(CommandId.FrameRaw, new byte[1025], fb));
    }

    [Theory]
    [InlineData(10, 5, 0, 0)]      // x1 < x0
    [InlineData(0, 128, 0, 0)]     // x1 fora do painel
    [InlineData(0, 10, 0, 8)]      // page fora do painel
    [InlineData(0, 10, 5, 2)]      // p1 < p0
    public void DeltaWithInvalidRect_IsRejected(int x0, int x1, int p0, int p1)
    {
        var decoder = new FrameDecoder();
        var fb = new byte[DisplayGeometry.FrameBytes];
        var payload = new byte[4 + 64];
        payload[0] = (byte)x0; payload[1] = (byte)x1; payload[2] = (byte)p0; payload[3] = (byte)p1;

        FrameDecodeResult result = decoder.Apply(CommandId.FrameDelta, payload, fb);
        Assert.True(result is FrameDecodeResult.BadRect or FrameDecodeResult.BadLength);
    }

    [Fact]
    public void DeltaWithTruncatedPayload_IsRejected()
    {
        var decoder = new FrameDecoder();
        var fb = new byte[DisplayGeometry.FrameBytes];
        // Retangulo declara 10x2 = 20 bytes, mas so vem 5.
        byte[] payload = { 0, 9, 0, 1, 1, 2, 3, 4, 5 };
        Assert.Equal(FrameDecodeResult.BadLength, decoder.Apply(CommandId.FrameDelta, payload, fb));
    }

    [Fact]
    public void DeltaHeaderShorterThanFourBytes_IsRejected()
    {
        var decoder = new FrameDecoder();
        var fb = new byte[DisplayGeometry.FrameBytes];
        Assert.Equal(FrameDecodeResult.BadLength, decoder.Apply(CommandId.FrameDelta, new byte[3], fb));
    }

    [Fact]
    public void RleFrameThatDoesNotExpandTo1024_IsRejected()
    {
        var decoder = new FrameDecoder();
        var fb = new byte[DisplayGeometry.FrameBytes];
        byte[] tooShort = { 0x80, 0x00 };   // expande para apenas 2 bytes
        Assert.Equal(FrameDecodeResult.BadLength, decoder.Apply(CommandId.FrameRle, tooShort, fb));
    }

    [Fact]
    public void NonFrameCommand_IsRejected()
    {
        var decoder = new FrameDecoder();
        Assert.Equal(FrameDecodeResult.NotAFrame,
            decoder.Apply(CommandId.Ping, new byte[4], new byte[DisplayGeometry.FrameBytes]));
    }

    private static byte[] Mutate(byte[] frame, int changes)
    {
        var copy = (byte[])frame.Clone();
        var rng = new Random(changes + 1);
        for (int i = 0; i < changes; i++) copy[rng.Next(copy.Length)] ^= 0xFF;
        return copy;
    }
}
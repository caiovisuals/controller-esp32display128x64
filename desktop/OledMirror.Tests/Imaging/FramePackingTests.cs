using OledMirror.Core.Imaging;

namespace OledMirror.Tests.Imaging;

public class FramePackingTests {
    [Fact]
    public void Geometry_Is128x64And1024Bytes()
    {
        Assert.Equal(128, DisplayGeometry.Width);
        Assert.Equal(64, DisplayGeometry.Height);
        Assert.Equal(8192, DisplayGeometry.PixelCount);
        Assert.Equal(1024, DisplayGeometry.FrameBytes);
        Assert.Equal(8, DisplayGeometry.Pages);
    }

    [Fact]
    public void ByteLayout_MatchesTheControllerGddram()
    {
        // Layout nativo do SSD1306/SH1106: byte = page*128 + x, bit = y%8,
        // com o bit 0 na linha de cima. E' o que permite ao firmware fazer
        // memcpy direto do payload para o buffer do display.
        Assert.Equal(0, DisplayGeometry.ByteIndex(0, 0));
        Assert.Equal(0, DisplayGeometry.ByteIndex(0, 7));
        Assert.Equal(128, DisplayGeometry.ByteIndex(0, 8));
        Assert.Equal(127, DisplayGeometry.ByteIndex(127, 0));
        Assert.Equal(1023, DisplayGeometry.ByteIndex(127, 63));

        Assert.Equal(0x01, DisplayGeometry.BitMask(0));
        Assert.Equal(0x80, DisplayGeometry.BitMask(7));
        Assert.Equal(0x01, DisplayGeometry.BitMask(8));
    }

    [Fact]
    public void EveryPixelMapsToADistinctBit()
    {
        var seen = new HashSet<(int Byte, int Bit)>();
        for (int y = 0; y < DisplayGeometry.Height; y++)
            for (int x = 0; x < DisplayGeometry.Width; x++)
                Assert.True(seen.Add((DisplayGeometry.ByteIndex(x, y), DisplayGeometry.BitMask(y))),
                            $"Colisao no pixel ({x},{y}).");

        Assert.Equal(DisplayGeometry.PixelCount, seen.Count);
    }

    [Fact]
    public void SetAndGetPixel_RoundTrip()
    {
        var frame = new byte[DisplayGeometry.FrameBytes];
        var rng = new Random(5);
        var expected = new bool[DisplayGeometry.Width, DisplayGeometry.Height];

        for (int i = 0; i < 3000; i++)
        {
            int x = rng.Next(DisplayGeometry.Width);
            int y = rng.Next(DisplayGeometry.Height);
            bool on = rng.Next(2) == 0;
            DisplayGeometry.SetPixel(frame, x, y, on);
            expected[x, y] = on;
        }

        for (int y = 0; y < DisplayGeometry.Height; y++)
            for (int x = 0; x < DisplayGeometry.Width; x++)
                Assert.Equal(expected[x, y], DisplayGeometry.GetPixel(frame, x, y));
    }

    [Fact]
    public void UnpackReversesPacking()
    {
        var frame = new byte[DisplayGeometry.FrameBytes];
        new Random(9).NextBytes(frame);

        var gray = new byte[DisplayGeometry.PixelCount];
        MonoFrameUtils.Unpack(frame, gray);

        for (int y = 0; y < DisplayGeometry.Height; y++)
            for (int x = 0; x < DisplayGeometry.Width; x++)
                Assert.Equal(DisplayGeometry.GetPixel(frame, x, y) ? 255 : 0, gray[y * DisplayGeometry.Width + x]);
    }

    [Fact]
    public void CountLitPixels_CountsBits()
    {
        var frame = new byte[DisplayGeometry.FrameBytes];
        Assert.Equal(0, MonoFrameUtils.CountLitPixels(frame));

        Array.Fill(frame, (byte)0xFF);
        Assert.Equal(DisplayGeometry.PixelCount, MonoFrameUtils.CountLitPixels(frame));
    }
}
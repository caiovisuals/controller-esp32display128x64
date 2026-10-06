namespace OledMirror.Core.Imaging;

/// <summary>
/// Geometria do painel e o layout nativo da GDDRAM do SSD1306/SH1106:
/// byte = pagina * 128 + x, bit = y % 8 (bit 0 = linha de cima), 1 = aceso.
/// E' o mesmo layout do buffer do U8g2, o que faz o firmware aplicar um frame com um memcpy.
/// </summary>
public static class DisplayGeometry
{
    public const int Width = 128;
    public const int Height = 64;
    public const int Pages = Height / 8;
    public const int PixelCount = Width * Height;
    public const int FrameBytes = PixelCount / 8;

    public static int ByteIndex(int x, int y) => (y >> 3) * Width + x;

    public static byte BitMask(int y) => (byte)(1 << (y & 7));

    public static bool GetPixel(ReadOnlySpan<byte> frame, int x, int y)
        => (frame[ByteIndex(x, y)] & BitMask(y)) != 0;

    public static void SetPixel(Span<byte> frame, int x, int y, bool on)
    {
        int index = ByteIndex(x, y);
        if (on) frame[index] |= BitMask(y);
        else frame[index] &= (byte)~BitMask(y);
    }
}
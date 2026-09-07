using OledMirror.Core.Imaging;

namespace OledMirror.Tests.Imaging;

public class ResizeTests {
    private static (int dx, int dy, int dw, int dh) Dest(int w, int h, ResizeMode mode)
    {
        Rescaler.ComputeRects(w, h, mode, out _, out _, out _, out _,
                              out int dx, out int dy, out int dw, out int dh);
        return (dx, dy, dw, dh);
    }

    private static (int sx, int sy, int sw, int sh) Src(int w, int h, ResizeMode mode)
    {
        Rescaler.ComputeRects(w, h, mode, out int sx, out int sy, out int sw, out int sh,
                              out _, out _, out _, out _);
        return (sx, sy, sw, sh);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    [InlineData(3440, 1440)]   // ultrawide 21:9
    [InlineData(5120, 1440)]   // super ultrawide 32:9
    [InlineData(1280, 1024)]   // 5:4
    [InlineData(1080, 1920)]   // monitor em pe
    public void Stretch_AlwaysFillsTheWholePanel(int w, int h)
    {
        Assert.Equal((0, 0, 128, 64), Dest(w, h, ResizeMode.Stretch));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(3440, 1440)]
    [InlineData(1080, 1920)]
    public void Fit_KeepsAspectRatioAndNeverOverflows(int w, int h)
    {
        (int dx, int dy, int dw, int dh) = Dest(w, h, ResizeMode.Fit);

        Assert.True(dw <= 128 && dh <= 64);
        Assert.True(dx >= 0 && dy >= 0);
        Assert.True(dx + dw <= 128 && dy + dh <= 64);

        double sourceAspect = (double)w / h;
        double destAspect = (double)dw / dh;
        Assert.True(Math.Abs(sourceAspect - destAspect) / sourceAspect < 0.03,
                    $"Proporcao distorcida: {sourceAspect:F3} -> {destAspect:F3}");

        // "Fit" quer dizer que nada da origem e' cortado.
        Assert.Equal((0, 0, w, h), Src(w, h, ResizeMode.Fit));
    }

    [Fact]
    public void Fit_On16By9_LeavesPillarboxBars()
    {
        // 1920x1080 -> escala por min(128/1920, 64/1080) = 0,0593 -> 114x64.
        (int dx, _, int dw, int dh) = Dest(1920, 1080, ResizeMode.Fit);
        Assert.Equal(64, dh);
        Assert.InRange(dw, 112, 115);
        Assert.True(dx > 0, "Deveria sobrar barra nas laterais.");
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    [InlineData(3440, 1440)]
    public void Crop_FillsThePanelByCroppingTheSource(int w, int h)
    {
        Assert.Equal((0, 0, 128, 64), Dest(w, h, ResizeMode.Crop));

        (int sx, int sy, int sw, int sh) = Src(w, h, ResizeMode.Crop);
        Assert.True(sw <= w && sh <= h);
        Assert.True(sx >= 0 && sy >= 0 && sx + sw <= w && sy + sh <= h);

        // O recorte tem que ficar em 2:1 para nao distorcer.
        Assert.True(Math.Abs((double)sw / sh - 2.0) < 0.02, $"Recorte em {(double)sw / sh:F3}:1");
    }

    [Fact]
    public void Crop_On16By9_CutsTopAndBottom_NotTheSides()
    {
        (int sx, int sy, int sw, int sh) = Src(1920, 1080, ResizeMode.Crop);
        Assert.Equal(0, sx);
        Assert.Equal(1920, sw);       // largura inteira preservada
        Assert.Equal(960, sh);        // 1920/2
        Assert.Equal(60, sy);         // (1080-960)/2
    }

    [Fact]
    public void Letterbox_AlwaysUsesTheFullWidth()
    {
        foreach ((int w, int h) in new[] { (1920, 1080), (3440, 1440), (5120, 1440), (1280, 1024) })
        {
            (int dx, _, int dw, _) = Dest(w, h, ResizeMode.Letterbox);
            Assert.Equal(0, dx);
            Assert.Equal(128, dw);
        }
    }

    [Fact]
    public void Letterbox_OnUltrawide_AddsHorizontalBars()
    {
        // 5120x1440 e' 3,56:1, mais largo que o painel 2:1 -> sobra em cima e embaixo.
        (_, int dy, _, int dh) = Dest(5120, 1440, ResizeMode.Letterbox);
        Assert.True(dh < 64, "Esperava barras.");
        Assert.True(dy > 0);
    }

    [Fact]
    public void SolidWhiteSource_ProducesAllBitsSet()
    {
        byte[] frame = Process(new TestFrame(256, 128, 255), ResizeMode.Stretch, DitheringMode.Threshold);
        Assert.All(frame, b => Assert.Equal(0xFF, b));
        Assert.Equal(DisplayGeometry.PixelCount, MonoFrameUtils.CountLitPixels(frame));
    }

    [Fact]
    public void SolidBlackSource_ProducesAllBitsClear()
    {
        byte[] frame = Process(new TestFrame(256, 128, 0), ResizeMode.Stretch, DitheringMode.Threshold);
        Assert.All(frame, b => Assert.Equal(0x00, b));
        Assert.Equal(0, MonoFrameUtils.CountLitPixels(frame));
    }

    [Fact]
    public void SolidSource_StillProduces1024Bytes()
    {
        Assert.Equal(1024, Process(new TestFrame(1920, 1080, 200), ResizeMode.Fit, DitheringMode.FloydSteinberg).Length);
    }

    [Theory]
    [InlineData(ResizeMode.Stretch)]
    [InlineData(ResizeMode.Fit)]
    [InlineData(ResizeMode.Crop)]
    [InlineData(ResizeMode.Letterbox)]
    public void EveryResizeMode_HandlesEveryCommonResolution(ResizeMode mode)
    {
        foreach ((int w, int h) in new[] { (1920, 1080), (2560, 1440), (3840, 2160), (3440, 1440), (1080, 1920), (800, 600), (17, 9) })
        {
            byte[] frame = Process(new TestFrame(w, h, 128), mode, DitheringMode.BayerOrdered4x4);
            Assert.Equal(1024, frame.Length);
        }
    }

    [Fact]
    public void FitPadding_IsBlackByDefault_AndWhiteWhenRequested()
    {
        var source = new TestFrame(1920, 1080, 255);

        var options = new ImageProcessorOptions
        {
            ResizeMode = ResizeMode.Fit,
            Dithering = DitheringMode.Threshold,
            AutoContrast = false,
        };
        var processor = new FrameProcessor(options);
        var frame = new byte[DisplayGeometry.FrameBytes];

        processor.Process(source.ToCapturedFrame(), frame);
        Assert.False(DisplayGeometry.GetPixel(frame, 0, 32));      // barra apagada
        Assert.True(DisplayGeometry.GetPixel(frame, 64, 32));      // conteudo aceso

        options.PadWhite = true;
        processor.Process(source.ToCapturedFrame(), frame);
        Assert.True(DisplayGeometry.GetPixel(frame, 0, 32));       // barra acesa
    }

    private static byte[] Process(TestFrame source, ResizeMode resize, DitheringMode dithering)
    {
        var processor = new FrameProcessor(new ImageProcessorOptions
        {
            ResizeMode = resize,
            Dithering = dithering,
            AutoContrast = false,
        });
        var frame = new byte[DisplayGeometry.FrameBytes];
        processor.Process(source.ToCapturedFrame(), frame);
        return frame;
    }
}

/// <summary>Frame BGRA sintetico simples para os testes.</summary>
internal sealed class TestFrame {
    private readonly byte[] _buffer;

    public TestFrame(int width, int height, byte level)
    {
        Width = width;
        Height = height;
        _buffer = new byte[width * height * 4];
        for (int i = 0; i < _buffer.Length; i += 4)
        {
            _buffer[i] = _buffer[i + 1] = _buffer[i + 2] = level;
            _buffer[i + 3] = 255;
        }
    }

    public TestFrame(int width, int height, Func<int, int, (byte B, byte G, byte R)> painter)
    {
        Width = width;
        Height = height;
        _buffer = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                (byte b, byte g, byte r) = painter(x, y);
                int p = (y * width + x) * 4;
                _buffer[p] = b; _buffer[p + 1] = g; _buffer[p + 2] = r; _buffer[p + 3] = 255;
            }
    }

    public int Width { get; }
    public int Height { get; }

    public CapturedFrame ToCapturedFrame() => new(_buffer, Width, Height, Width * 4);
}
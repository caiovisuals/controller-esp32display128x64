using OledMirror.Core.Imaging;

namespace OledMirror.Tests.Imaging;

public class DitheringTests {
    private static byte[] Dither(byte grayLevel, DitheringMode mode, int threshold = 128)
    {
        var gray = new byte[DisplayGeometry.PixelCount];
        Array.Fill(gray, grayLevel);
        var frame = new byte[DisplayGeometry.FrameBytes];
        new Ditherer().DitherAndPack(gray, frame, new ImageProcessorOptions { Dithering = mode, Threshold = threshold });
        return frame;
    }

    [Theory]
    [InlineData(DitheringMode.Threshold)]
    [InlineData(DitheringMode.FloydSteinberg)]
    [InlineData(DitheringMode.BayerOrdered4x4)]
    [InlineData(DitheringMode.BayerOrdered8x8)]
    [InlineData(DitheringMode.Atkinson)]
    public void EveryAlgorithm_ProducesExactly1024Bytes(DitheringMode mode)
    {
        Assert.Equal(1024, Dither(200, mode).Length);
    }

    [Theory]
    [InlineData(DitheringMode.Threshold)]
    [InlineData(DitheringMode.FloydSteinberg)]
    [InlineData(DitheringMode.BayerOrdered4x4)]
    [InlineData(DitheringMode.BayerOrdered8x8)]
    [InlineData(DitheringMode.Atkinson)]
    public void PureBlackAndPureWhite_AreNeverDithered(DitheringMode mode)
    {
        // Um extremo nao pode virar textura: 0 e' apagado, 255 e' aceso, sempre.
        Assert.Equal(0, MonoFrameUtils.CountLitPixels(Dither(0, mode)));
        Assert.Equal(DisplayGeometry.PixelCount, MonoFrameUtils.CountLitPixels(Dither(255, mode)));
    }

    [Fact]
    public void Threshold_IsAHardCut()
    {
        Assert.Equal(0, MonoFrameUtils.CountLitPixels(Dither(128, DitheringMode.Threshold, 128)));
        Assert.Equal(DisplayGeometry.PixelCount, MonoFrameUtils.CountLitPixels(Dither(129, DitheringMode.Threshold, 128)));
    }

    [Theory]
    [InlineData(DitheringMode.FloydSteinberg)]
    [InlineData(DitheringMode.BayerOrdered4x4)]
    [InlineData(DitheringMode.BayerOrdered8x8)]
    public void MidGray_ProducesRoughlyHalfLitPixels(DitheringMode mode)
    {
        // O ponto do dithering: a densidade media de pixels acesos tem que
        // acompanhar o nivel de cinza. 50% de cinza -> ~50% de pixels acesos.
        int lit = MonoFrameUtils.CountLitPixels(Dither(128, mode));
        double ratio = (double)lit / DisplayGeometry.PixelCount;
        Assert.InRange(ratio, 0.40, 0.60);
    }

    [Theory]
    [InlineData(64, 0.15, 0.40)]
    [InlineData(192, 0.60, 0.85)]
    public void FloydSteinberg_TracksTheInputLevel(byte level, double min, double max)
    {
        int lit = MonoFrameUtils.CountLitPixels(Dither(level, DitheringMode.FloydSteinberg));
        Assert.InRange((double)lit / DisplayGeometry.PixelCount, min, max);
    }

    [Fact]
    public void Bayer4x4_RepeatsEveryFourPixels()
    {
        byte[] frame = Dither(128, DitheringMode.BayerOrdered4x4);

        // O dither ordenado e' periodico: e' isso que o torna estavel no tempo,
        // ao contrario da difusao de erro, que "ferve" quando a tela se mexe.
        for (int y = 0; y + 4 < DisplayGeometry.Height; y++)
            for (int x = 0; x + 4 < DisplayGeometry.Width; x++)
                Assert.Equal(DisplayGeometry.GetPixel(frame, x, y),
                             DisplayGeometry.GetPixel(frame, x + 4, y + 4));
    }

    [Fact]
    public void Invert_FlipsEveryBit()
    {
        var gray = new byte[DisplayGeometry.PixelCount];
        new Random(4).NextBytes(gray);

        var normal = new byte[DisplayGeometry.FrameBytes];
        var inverted = new byte[DisplayGeometry.FrameBytes];
        var ditherer = new Ditherer();

        ditherer.DitherAndPack(gray, normal, new ImageProcessorOptions { Dithering = DitheringMode.BayerOrdered4x4 });
        ditherer.DitherAndPack(gray, inverted, new ImageProcessorOptions { Dithering = DitheringMode.BayerOrdered4x4, Invert = true });

        for (int i = 0; i < normal.Length; i++) Assert.Equal((byte)~normal[i], inverted[i]);
    }

    [Fact]
    public void DithererIsReusable_AndDoesNotLeakErrorBetweenFrames()
    {
        // O buffer de erro da difusao precisa ser zerado a cada frame: senao o
        // segundo frame sai diferente do primeiro para a mesma entrada.
        var ditherer = new Ditherer();
        var gray = new byte[DisplayGeometry.PixelCount];
        Array.Fill(gray, (byte)100);
        var options = new ImageProcessorOptions { Dithering = DitheringMode.FloydSteinberg };

        var first = new byte[DisplayGeometry.FrameBytes];
        var second = new byte[DisplayGeometry.FrameBytes];

        ditherer.DitherAndPack(gray, first, options);
        // Frame diferente no meio, para sujar o estado interno.
        new Random(1).NextBytes(gray);
        ditherer.DitherAndPack(gray, new byte[DisplayGeometry.FrameBytes], options);
        Array.Fill(gray, (byte)100);
        ditherer.DitherAndPack(gray, second, options);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CheckerboardSource_ProducesTheExactExpectedPattern()
    {
        // Fonte 128x64 mapeada 1:1: cada pixel de destino vem de um pixel de
        // origem, entao o resultado e' totalmente previsivel.
        const int cell = 8;
        var source = new TestFrame(128, 64, (x, y) =>
        {
            byte v = ((x / cell + y / cell) & 1) == 0 ? (byte)0 : (byte)255;
            return (v, v, v);
        });

        var processor = new FrameProcessor(new ImageProcessorOptions
        {
            ResizeMode = ResizeMode.Stretch,
            Dithering = DitheringMode.Threshold,
            AutoContrast = false,
        });
        var frame = new byte[DisplayGeometry.FrameBytes];
        processor.Process(source.ToCapturedFrame(), frame);

        for (int y = 0; y < DisplayGeometry.Height; y++)
            for (int x = 0; x < DisplayGeometry.Width; x++)
                Assert.Equal(((x / cell + y / cell) & 1) == 1, DisplayGeometry.GetPixel(frame, x, y));

        Assert.Equal(DisplayGeometry.PixelCount / 2, MonoFrameUtils.CountLitPixels(frame));
    }

    [Fact]
    public void AutoContrast_OpensUpALowContrastImage()
    {
        // Gradiente estreito e escuro (100..116): fica inteiro abaixo do limiar
        // de 128, entao sem auto-contraste o painel apaga por completo e o
        // conteudo some. Com auto-contraste a faixa e' esticada para 0..255 e a
        // mesma imagem passa a ter detalhe.
        var source = new TestFrame(1280, 640, (x, _) =>
        {
            byte v = (byte)(100 + x * 16 / 1280);
            return (v, v, v);
        });

        var options = new ImageProcessorOptions
        {
            ResizeMode = ResizeMode.Stretch,
            Dithering = DitheringMode.Threshold,
            AutoContrast = false,
        };
        var processor = new FrameProcessor(options);
        var frame = new byte[DisplayGeometry.FrameBytes];

        processor.Process(source.ToCapturedFrame(), frame);
        int litWithout = MonoFrameUtils.CountLitPixels(frame);

        options.AutoContrast = true;
        processor.Process(source.ToCapturedFrame(), frame);
        int litWith = MonoFrameUtils.CountLitPixels(frame);

        Assert.Equal(0, litWithout);                       // toda a faixa esta abaixo do limiar
        Assert.InRange((double)litWith / DisplayGeometry.PixelCount, 0.35, 0.65);
    }
}
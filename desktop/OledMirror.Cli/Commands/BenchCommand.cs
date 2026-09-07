using System.Diagnostics;
using System.Globalization;
using OledMirror.Core.Capture;
using OledMirror.Core.Imaging;
using OledMirror.Core.Protocol;

namespace OledMirror.Cli.Commands;

/// <summary>
/// Mede, com conteudo realista, quanto cada estrategia de codificacao realmente
/// economiza. A decisao sobre compressao no projeto vem destes numeros, nao de
/// intuicao - ver docs/PERFORMANCE.md.
/// </summary>
internal static class BenchCommand
{
    /// <summary>Como o tempo avanca dentro do cenario.</summary>
    private enum TimeMode
    {
        /// <summary>Tempo congelado: a tela nao muda em nada.</summary>
        Frozen,
        /// <summary>Tempo em passos de 1 s: so o relogio muda.</summary>
        PerSecond,
        /// <summary>Tempo continuo a 10 FPS.</summary>
        Continuous,
    }

    private sealed record Scenario(string Name, string Description, Func<ICaptureSource> Factory,
                                   TimeMode Time, int Frames);

    public static int Run(string[] args)
    {
        var o = new Options(args);
        if (o.Has("help")) { Usage(); return 0; }

        int baud = o.GetInt("baud", 921600);
        int frames = o.GetInt("frames", 120);
        var dither = o.GetEnum("dither", DitheringMode.FloydSteinberg);
        int width = o.GetInt("width", 1920);
        int height = o.GetInt("height", 1080);

        var scenarios = new[]
        {
            new Scenario("tela parada", "desktop imovel: nada muda entre frames",
                () => new MockDesktopSource(width, height) { AnimateCursor = false },
                TimeMode.Frozen, frames),
            new Scenario("relogio", "tela parada, so o relogio muda (1x por segundo)",
                () => new MockDesktopSource(width, height) { AnimateCursor = false },
                TimeMode.PerSecond, frames),
            new Scenario("cursor", "desktop com o mouse se movendo",
                () => new MockDesktopSource(width, height),
                TimeMode.Continuous, frames),
            new Scenario("rolagem", "janela rolando: quase tudo muda",
                () => new MockDesktopSource(width, height) { ScrollContent = true },
                TimeMode.Continuous, frames),
            new Scenario("animacao", "objeto grande em movimento",
                () => new TestPatternSource(TestPattern.OrbitingCircle, width, height),
                TimeMode.Continuous, frames),
            new Scenario("ruido", "pior caso: frame incompressivel novo a cada tick",
                () => new TestPatternSource(TestPattern.Noise, width, height),
                TimeMode.Continuous, frames),
            new Scenario("detalhe fino", "listras de 1 px: somem no downscale e viram tela estatica",
                () => new TestPatternSource(TestPattern.FineLines, width, height),
                TimeMode.Continuous, frames),
        };

        Console.WriteLine($"Benchmark de codificacao - origem {width}x{height}, dither {dither}, {frames} frames por cenario");
        Console.WriteLine($"Frame cru = {DisplayGeometry.FrameBytes} bytes | pacote cru = {PacketEncoder.PacketSize(DisplayGeometry.FrameBytes)} bytes no fio");
        Console.WriteLine($"Serial a {baud} baud 8N1 = {baud / 10} bytes/s\n");

        Console.WriteLine($"{"cenario",-12} {"raw B/f",8} {"rle B/f",8} {"delta B/f",10} {"auto B/f",9} {"ganho",7} {"FPS max",8}  observacao");
        Console.WriteLine(new string('-', 100));

        foreach (Scenario scenario in scenarios)
        {
            byte[][] sequence = BuildSequence(scenario, dither);

            double raw = Measure(sequence, FrameEncoding.Raw);
            double rle = Measure(sequence, FrameEncoding.Rle);
            double delta = Measure(sequence, FrameEncoding.DeltaRle);
            double auto = Measure(sequence, FrameEncoding.Auto);

            // Bytes por frame no fio, ja com os 10 bytes de enquadramento.
            double onWire = auto + ProtocolConstants.OverheadSize;
            double maxFps = onWire <= 0 ? double.PositiveInfinity : baud / 10.0 / onWire;
            double gain = raw / Math.Max(1e-9, auto);

            Console.WriteLine(
                $"{scenario.Name,-12} {raw,8:F0} {rle,8:F0} {delta,10:F0} {auto,9:F0} " +
                $"{gain,6:F1}x {FormatFps(maxFps),8}  {scenario.Description}");
        }

        Console.WriteLine();
        Console.WriteLine("Notas:");
        Console.WriteLine("  * 'B/f' e' a media de bytes de payload por frame produzido, contando frames");
        Console.WriteLine("    identicos como 0 bytes (nao sao transmitidos).");
        Console.WriteLine("  * 'FPS max' e' o teto imposto so pela serial. O I2C do painel impoe outro");
        Console.WriteLine("    teto, independente deste - ver docs/PERFORMANCE.md.");

        return 0;
    }

    private static string FormatFps(double fps)
        => double.IsInfinity(fps) || fps > 9999 ? ">9999" : fps.ToString("F0", CultureInfo.InvariantCulture);

    private static byte[][] BuildSequence(Scenario scenario, DitheringMode dither)
    {
        using ICaptureSource source = scenario.Factory();
        var processor = new FrameProcessor(new ImageProcessorOptions
        {
            ResizeMode = ResizeMode.Fit,
            Dithering = dither,
            AutoContrast = false,
        });

        var frames = new byte[scenario.Frames][];
        for (int i = 0; i < scenario.Frames; i++)
        {
            double t = scenario.Time switch
            {
                TimeMode.Frozen => 3.0,                 // sempre o mesmo instante
                TimeMode.PerSecond => Math.Floor(i / 10.0),
                _ => i / 10.0,                          // 10 FPS de tempo simulado
            };
            switch (source)
            {
                case MockDesktopSource m: m.FixedTimeSeconds = t; break;
                case TestPatternSource p: p.FixedTimeSeconds = t; break;
            }

            source.TryCapture(out CapturedFrame captured);
            frames[i] = new byte[DisplayGeometry.FrameBytes];
            processor.Process(captured, frames[i]);
        }

        return frames;
    }

    private static double Measure(byte[][] frames, FrameEncoding encoding)
    {
        var encoder = new FrameEncoder { Preference = encoding, SkipUnchangedFrames = true };
        long total = 0;

        foreach (byte[] frame in frames)
            if (encoder.TryEncode(frame, out _, out ReadOnlyMemory<byte> payload)) total += payload.Length;

        return (double)total / frames.Length;
    }

    private static void Usage() => Console.WriteLine("""
        bench - compara RAW vs RLE vs DELTA em conteudo realista

          --frames <n>       Frames por cenario                 (padrao: 120)
          --baud <n>         Baud usado no calculo de FPS       (padrao: 921600)
          --dither <modo>    Algoritmo de dithering             (padrao: FloydSteinberg)
          --width/--height   Resolucao de origem                (padrao: 1920x1080)
        """);
}
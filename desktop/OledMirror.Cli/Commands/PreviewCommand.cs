using System.Diagnostics;
using OledMirror.Core.Capture;
using OledMirror.Core.Imaging;

namespace OledMirror.Cli.Commands;

/// <summary>
/// Mostra no terminal exatamente o que iria para o painel
/// Serve para comparar modos de resize e algoritmos de dithering sem hardware nenhum
/// </summary>
internal static class PreviewCommand
{
    public static int Run(string[] args)
    {
        var o = new Options(args);
        if (o.Has("help")) { Usage(); return 0; }

        var pattern = o.GetEnum("pattern", TestPattern.Text);
        var resize = o.GetEnum("resize", ResizeMode.Fit);
        var dither = o.GetEnum("dither", DitheringMode.FloydSteinberg);
        int width = o.GetInt("width", 1920);
        int height = o.GetInt("height", 1080);

        var options = new ImageProcessorOptions
        {
            ResizeMode = resize,
            Dithering = dither,
            Threshold = o.GetInt("threshold", 128),
            AutoContrast = !o.Has("no-autocontrast"),
            Contrast = o.GetDouble("contrast", 1.0),
            Gamma = o.GetDouble("gamma", 1.0),
            Invert = o.Has("invert"),
        };

        using var source = new TestPatternSource(pattern, width, height) { FixedTimeSeconds = o.GetDouble("time", 1.0) };
        var processor = new FrameProcessor(options);
        var frame = new byte[DisplayGeometry.FrameBytes];

        source.TryCapture(out CapturedFrame captured);

        var sw = Stopwatch.StartNew();
        const int iterations = 50;
        for (int i = 0; i < iterations; i++) processor.Process(captured, frame);
        sw.Stop();

        Console.WriteLine($"origem  : {width}x{height} ({pattern})");
        Console.WriteLine($"resize  : {resize}");
        Console.WriteLine($"dither  : {dither}   auto-contraste: {(options.AutoContrast ? "sim" : "nao")}");
        Console.WriteLine($"saida   : 128x64, {frame.Length} bytes, {MonoFrameUtils.CountLitPixels(frame)} pixels acesos");
        Console.WriteLine($"tempo   : {sw.Elapsed.TotalMilliseconds / iterations:F2} ms por frame (media de {iterations})");
        Console.WriteLine();
        Console.WriteLine("+" + new string('-', DisplayGeometry.Width) + "+");
        foreach (string line in MonoFrameUtils.ToHalfBlocks(frame).Split('\n'))
        {
            if (line.Length == 0) continue;
            Console.WriteLine("|" + line + "|");
        }
        Console.WriteLine("+" + new string('-', DisplayGeometry.Width) + "+");

        return 0;
    }

    private static void Usage() => Console.WriteLine("""
        preview - processa um padrao de teste e mostra o resultado 128x64

          --pattern <nome>     Checkerboard | BouncingBox | OrbitingCircle | Gradient |
                               Text | FineLines | Black | White        (padrao: Text)
          --resize <modo>      Stretch | Fit | Crop | Letterbox        (padrao: Fit)
          --dither <modo>      Threshold | FloydSteinberg | BayerOrdered4x4 |
                               BayerOrdered8x8 | Atkinson              (padrao: FloydSteinberg)
          --width / --height   Resolucao de origem                     (padrao: 1920x1080)
          --threshold <0-255>  Limiar                                  (padrao: 128)
          --contrast <n>       Ganho de contraste                      (padrao: 1.0)
          --gamma <n>          Correcao gamma                          (padrao: 1.0)
          --no-autocontrast    Desliga a normalizacao automatica
          --invert             Inverte a saida
          --time <segundos>    Instante da animacao                    (padrao: 1.0)
        """);
}
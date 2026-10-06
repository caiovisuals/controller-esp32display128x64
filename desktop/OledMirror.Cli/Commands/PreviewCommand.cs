using System.Diagnostics;
using OledMirror.Core.Capture;
using OledMirror.Core.Configuration;
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
        if (o.Get("text") is string text) return RunText(o, text);

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
        PrintFrame(frame);

        return 0;
    }

    /// <summary>Modo texto: o que o painel mostraria com "Mostrar um texto" na aplicacao.</summary>
    private static int RunText(Options o, string text)
    {
        var settings = new AppSettings
        {
            ContentMode = ContentMode.Text,
            DisplayText = text.Replace("\\n", "\n"),
            TextScale = o.GetInt("scale", 0),
            TextAlign = o.GetEnum("align", TextAlign.Center),
            Invert = o.Has("invert"),
        };
        settings.Normalize();

        using var source = new TextSource(settings.ToTextOptions()) { FixedTimeSeconds = o.GetDouble("time", 0) };
        var frame = new byte[DisplayGeometry.FrameBytes];
        source.TryCapture(out CapturedFrame captured);
        new FrameProcessor(settings.ToMirrorSettings().Image).Process(captured, frame);

        Console.WriteLine($"texto   : tamanho {source.Scale}, {source.Lines.Count} linha(s){(source.Scrolls ? ", rolando" : "")}");
        PrintFrame(frame);
        return 0;
    }

    private static void PrintFrame(byte[] frame)
    {
        Console.WriteLine("+" + new string('-', DisplayGeometry.Width) + "+");
        foreach (string line in MonoFrameUtils.ToHalfBlocks(frame).Split('\n'))
        {
            if (line.Length == 0) continue;
            Console.WriteLine("|" + line + "|");
        }
        Console.WriteLine("+" + new string('-', DisplayGeometry.Width) + "+");
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

        preview --text "<texto>" - mostra um texto como no modo "Mostrar um texto"

          --scale <0-4>        Tamanho da fonte, 0 = automatico         (padrao: 0)
          --align <modo>       Left | Center | Right                   (padrao: Center)
          --invert             Fundo aceso
          --time <segundos>    Instante da rolagem, se nao couber       (padrao: 0)
          Use \n dentro do texto para quebrar a linha.
        """);
}
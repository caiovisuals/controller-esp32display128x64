using OledMirror.Core.Capture;
using OledMirror.Core.Devices;
using OledMirror.Core.Imaging;
using OledMirror.Core.Logging;
using OledMirror.Core.Pipeline;
using OledMirror.Core.Protocol;
using OledMirror.Core.Simulation;
using OledMirror.Core.Transport;

namespace OledMirror.Cli.Commands;

/// <summary>
/// Roda o pipeline inteiro contra um ESP32 simulado e imprime as metricas. E' a
/// forma de validar captura -> processamento -> protocolo -> painel sem hardware
/// </summary>
internal static class SimulateCommand
{
    public static int Run(string[] args)
    {
        var o = new Options(args);
        if (o.Has("help")) { Usage(); return 0; }

        int fps = o.GetInt("fps", 10);
        int seconds = o.GetInt("seconds", 5);
        var dither = o.GetEnum("dither", DitheringMode.FloydSteinberg);
        var resize = o.GetEnum("resize", ResizeMode.Fit);
        var encoding = o.GetEnum("encoding", FrameEncoding.Auto);
        bool renderDelay = o.Has("realistic");

        var log = new Logger { MinimumLevel = o.Has("verbose") ? LogLevel.Debug : LogLevel.Info };
        log.AddSink(new ConsoleLogSink());

        var deviceOptions = new SimulatedDeviceOptions { SimulateRenderDelay = renderDelay };
        SimulatedDevice? device = null;

        ITransport CreateTransport()
        {
            (LoopbackTransport host, LoopbackTransport peer) = LoopbackTransport.CreatePair();
            device = new SimulatedDevice(peer, log, deviceOptions);
            device.Start();
            return host;
        }

        using var link = new DeviceLink(CreateTransport, log, new DeviceLinkOptions { ReconnectDelayMs = 200 });
        link.Start();

        if (!Wait(() => link.State == LinkState.Connected, 5000))
        {
            Console.Error.WriteLine("Nao conectou ao dispositivo simulado.");
            return 2;
        }
        Console.WriteLine($"Dispositivo: {link.Device}\n");

        using var pipeline = new MirrorPipeline(link, log)
        {
            Settings = new MirrorSettings
            {
                TargetFps = fps,
                Encoding = encoding,
                Image = new ImageProcessorOptions { ResizeMode = resize, Dithering = dither },
            },
        };
        pipeline.SetSource(new MockDesktopSource(o.GetInt("width", 1920), o.GetInt("height", 1080)));
        pipeline.Start();

        Thread.Sleep(seconds * 1000);
        pipeline.Stop();
        Thread.Sleep(200);

        PrintReport(pipeline, link, device, seconds);

        if (o.Has("show"))
        {
            var frame = new byte[DisplayGeometry.FrameBytes];
            if (pipeline.TryCopyLatestFrame(frame))
            {
                Console.WriteLine("\nConteudo final do painel:");
                Console.WriteLine("+" + new string('-', DisplayGeometry.Width) + "+");
                foreach (string line in MonoFrameUtils.ToHalfBlocks(frame).Split('\n'))
                    if (line.Length > 0) Console.WriteLine("|" + line + "|");
                Console.WriteLine("+" + new string('-', DisplayGeometry.Width) + "+");
            }
        }

        link.Stop();
        device?.Dispose();
        return 0;
    }

    private static void PrintReport(MirrorPipeline pipeline, DeviceLink link, SimulatedDevice? device, int seconds)
    {
        PipelineStatistics p = pipeline.Statistics.Snapshot();
        LinkStatistics l = link.Statistics.Snapshot();
        EncoderStatistics e = pipeline.EncoderStatistics.Snapshot();

        Console.WriteLine();
        Console.WriteLine("--- pipeline -------------------------------------------------");
        Console.WriteLine($"  FPS pedido / efetivo   : {p.RequestedFps} / {p.EffectiveFps:F1}");
        Console.WriteLine($"  frames capturados      : {p.FramesCaptured}");
        Console.WriteLine($"  frames processados     : {p.FramesProcessed}");
        Console.WriteLine($"  frames enviados        : {p.FramesSent}");
        Console.WriteLine($"  frames identicos       : {p.FramesSkippedUnchanged} (nao transmitidos)");
        Console.WriteLine($"  tempos (ms)            : captura {p.CaptureMs:F2} | processo {p.ProcessMs:F2} | " +
                          $"codifica {p.EncodeMs:F2} | envia {p.SendMs:F2} | total {p.TotalMs:F2}");

        Console.WriteLine("--- codificacao ----------------------------------------------");
        Console.WriteLine($"  raw / rle / delta / deltaRle : {e.FramesRaw} / {e.FramesRle} / {e.FramesDelta} / {e.FramesDeltaRle}");
        Console.WriteLine($"  payload total          : {e.PayloadBytes} bytes " +
                          $"(equivalente cru: {e.RawEquivalentBytes}, razao {e.CompressionRatio:P1})");

        Console.WriteLine("--- link -----------------------------------------------------");
        Console.WriteLine($"  frames enviados / ack  : {l.FramesSent} / {l.FramesAcked}");
        Console.WriteLine($"  descartes de fluxo     : {l.FramesDroppedByFlowControl}");
        Console.WriteLine($"  timeouts de ACK / NACK : {l.FrameAckTimeouts} / {l.Nacks}");
        Console.WriteLine($"  latencia (ultima/media): {l.LastLatencyMs:F1} ms / {l.AverageLatencyMs:F1} ms");
        Console.WriteLine($"  bytes enviados         : {l.BytesSent} ({l.BytesSent / Math.Max(1, seconds)} B/s)");
        Console.WriteLine($"  parser                 : {link.ParserStatistics}");

        if (device is not null)
        {
            Console.WriteLine("--- dispositivo ----------------------------------------------");
            Console.WriteLine($"  frames aplicados       : {device.FramesApplied}");
            Console.WriteLine($"  frames rejeitados      : {device.FramesDropped}");
        }
    }

    private static bool Wait(Func<bool> condition, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private static void Usage() => Console.WriteLine("""
        simulate - roda o pipeline completo contra um ESP32 simulado

          --fps <n>          FPS solicitado                     (padrao: 10)
          --seconds <n>      Duracao do teste                   (padrao: 5)
          --resize <modo>    Modo de redimensionamento          (padrao: Fit)
          --dither <modo>    Algoritmo de dithering             (padrao: FloydSteinberg)
          --encoding <modo>  Raw | Rle | Delta | DeltaRle | Auto (padrao: Auto)
          --realistic        Aplica o atraso real de escrita do painel (~11,5 ms)
          --width/--height   Resolucao de origem                (padrao: 1920x1080)
          --show             Imprime o conteudo final do painel
          --verbose          Log em nivel debug
        """);
}
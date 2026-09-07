using OledMirror.Core.Capture;
using OledMirror.Core.Devices;
using OledMirror.Core.Imaging;
using OledMirror.Core.Logging;
using OledMirror.Core.Protocol;
using OledMirror.Core.Transport;

namespace OledMirror.Cli.Commands;

/// <summary>
/// Fala com um ESP32 real pela serial. Cobre as fases 1 a 3 do roteiro de
/// integracao (handshake, texto, padrao de teste) antes de existir interface grafica.
/// </summary>
internal static class DeviceCommand
{
    public static int Run(string[] args)
    {
        var o = new Options(args);
        if (o.Has("help")) { Usage(); return 0; }

        string? port = o.Get("port");
        if (port is null)
        {
            SerialPortDescriptor? guess = new BasicSerialPortScanner().Scan().FirstOrDefault(p => p.LooksLikeEsp32)
                                       ?? new BasicSerialPortScanner().Scan().FirstOrDefault();
            if (guess is null)
            {
                Console.Error.WriteLine("Nenhuma porta serial encontrada. Use --port.");
                return 2;
            }
            port = guess.PortName;
            Console.WriteLine($"Porta escolhida automaticamente: {port}");
        }

        var log = new Logger { MinimumLevel = o.Has("verbose") ? LogLevel.Debug : LogLevel.Info };
        log.AddSink(new ConsoleLogSink());

        var transportOptions = new SerialTransportOptions
        {
            PortName = port,
            BaudRate = o.GetInt("baud", 921600),
        };

        using var link = new DeviceLink(() => new SerialPortTransport(transportOptions), log,
            new DeviceLinkOptions { AutoReconnect = false, HandshakeAttempts = 3 });

        link.Start();

        if (!Wait(() => link.State is LinkState.Connected or LinkState.Faulted, 8000) || link.State != LinkState.Connected)
        {
            Console.Error.WriteLine($"Nao foi possivel conectar em {port}. Estado: {link.State}");
            Console.Error.WriteLine("Verifique: firmware gravado, baud correto, monitor serial fechado.");
            return 2;
        }

        Console.WriteLine($"\nConectado: {link.Device}\n");

        string action = o.Get("do") ?? "info";
        switch (action.ToLowerInvariant())
        {
            case "info":
                break;

            case "text":
                string text = o.Get("text") ?? "HELLO WORLD";
                link.SendText(text);
                Console.WriteLine($"Texto enviado: {text}");
                break;

            case "clear":
                link.SendClear();
                Console.WriteLine("Painel limpo.");
                break;

            case "pattern":
            {
                var pattern = o.GetEnum("pattern", TestPattern.Checkerboard);
                using var source = new TestPatternSource(pattern, 512, 256) { FixedTimeSeconds = 1.0 };
                var processor = new FrameProcessor(new ImageProcessorOptions
                {
                    ResizeMode = o.GetEnum("resize", ResizeMode.Fit),
                    Dithering = o.GetEnum("dither", DitheringMode.FloydSteinberg),
                });
                var frame = new byte[DisplayGeometry.FrameBytes];
                source.TryCapture(out CapturedFrame captured);
                processor.Process(captured, frame);

                link.SendStreamBegin();
                Thread.Sleep(50);
                bool sent = link.TrySendFrame(CommandId.FrameRaw, frame);
                Console.WriteLine(sent ? $"Padrao {pattern} enviado." : "Falha ao enviar o frame.");
                Console.WriteLine(MonoFrameUtils.ToHalfBlocks(frame));
                break;
            }

            case "stats":
                DeviceStats? stats = null;
                link.StatsReceived += s => stats = s;
                link.RequestStats();
                if (Wait(() => stats is not null, 2000))
                {
                    Console.WriteLine($"  frames aplicados : {stats!.FramesApplied}");
                    Console.WriteLine($"  frames rejeitados: {stats.FramesDropped}");
                    Console.WriteLine($"  erros de CRC     : {stats.CrcErrors}");
                    Console.WriteLine($"  resyncs          : {stats.Resyncs}");
                    Console.WriteLine($"  render           : {stats.LastRenderMicros} us");
                    Console.WriteLine($"  heap livre       : {stats.FreeHeapKb} KB");
                    Console.WriteLine($"  uptime           : {stats.UptimeSeconds} s");
                }
                else Console.Error.WriteLine("O dispositivo nao respondeu ao GET_STATS.");
                break;

            case "contrast":
                byte value = (byte)Math.Clamp(o.GetInt("value", 127), 0, 255);
                link.SendConfig(Payloads.BuildConfigByte(ConfigKey.Contrast, value));
                Console.WriteLine($"Contraste ajustado para {value}.");
                break;

            default:
                Console.Error.WriteLine($"Acao desconhecida: {action}");
                return 1;
        }

        Thread.Sleep(300);
        Console.WriteLine($"\nlink: {link.Statistics.PacketsSent} pacotes enviados, parser: {link.ParserStatistics}");
        link.Stop();
        return 0;
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
        device - fala com um ESP32 real pela serial

          --port <COMx>      Porta serial (padrao: detecta sozinho)
          --baud <n>         Baud rate                          (padrao: 921600)
          --do <acao>        info | text | clear | pattern | stats | contrast
          --text <texto>     Texto para a acao 'text'
          --pattern <nome>   Padrao para a acao 'pattern'
          --value <0-255>    Valor para a acao 'contrast'
          --verbose          Log em nivel debug

        Exemplos:
          oledmirror device --do info
          oledmirror device --do text --text "OLA MUNDO"
          oledmirror device --do pattern --pattern Checkerboard
        """);
}
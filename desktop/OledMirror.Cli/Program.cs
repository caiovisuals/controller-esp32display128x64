using OledMirror.Cli.Commands;

namespace OledMirror.Cli;

/// <summary>
/// Ferramenta de linha de comando do projeto. Existe por tres motivos:
/// validar o pipeline sem Windows e sem ESP32, medir a compressao com numeros
/// reais em vez de estimativas, e testar o hardware nas fases iniciais
/// (portas, texto no painel, padroes de teste) antes da interface grafica
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string command = args[0].ToLowerInvariant();
        string[] rest = args[1..];

        try
        {
            return command switch
            {
                "preview" => PreviewCommand.Run(rest),
                "bench" => BenchCommand.Run(rest),
                "simulate" => SimulateCommand.Run(rest),
                "ports" => PortsCommand.Run(rest),
                "device" => DeviceCommand.Run(rest),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"erro: {ex.Message}");
            return 2;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Comando desconhecido: {command}");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage() => Console.WriteLine("""
        OledMirror CLI - espelhamento de tela em OLED 128x64

        USO
          oledmirror <comando> [opcoes]

        COMANDOS
          preview    Processa um padrao de teste e mostra o resultado 128x64 no terminal.
          bench      Compara RAW vs RLE vs DELTA em varios tipos de conteudo.
          simulate   Roda o pipeline completo contra um ESP32 simulado.
          ports      Lista as portas seriais e marca as que parecem um ESP32.
          device     Fala com um ESP32 real: ping, texto, padrao de teste, stats.

        Use "oledmirror <comando> --help" para as opcoes de cada um.
        """);
}
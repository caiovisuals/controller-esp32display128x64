using OledMirror.Core.Devices;

namespace OledMirror.Cli.Commands;

internal static class PortsCommand
{
    public static int Run(string[] args)
    {
        IReadOnlyList<SerialPortDescriptor> ports = new BasicSerialPortScanner().Scan();

        if (ports.Count == 0)
        {
            Console.WriteLine("Nenhuma porta serial encontrada.");
            Console.WriteLine();
            Console.WriteLine("No Windows, verifique se o driver da ponte USB-UART esta instalado:");
            Console.WriteLine("  CP2102 -> driver da Silicon Labs   |   CH340/CH9102 -> driver da WCH");
            return 1;
        }

        Console.WriteLine($"{"PORTA",-12} {"PROVAVEL ESP32",-16} DESCRICAO");
        foreach (SerialPortDescriptor p in ports)
            Console.WriteLine($"{p.PortName,-12} {(p.LooksLikeEsp32 ? "sim" : "-"),-16} {p.Description}");

        return 0;
    }
}
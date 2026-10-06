using System.IO.Ports;

namespace OledMirror.Core.Devices;

/// <summary>Uma porta serial do sistema, com VID/PID quando se sabe.</summary>
public sealed record SerialPortDescriptor(string PortName, string Description, string HardwareId = "", ushort Vid = 0, ushort Pid = 0)
{
    /// <summary>VIDs das pontes USB-UART usadas em placas ESP32, e o da propria Espressif (USB nativo).</summary>
    private static readonly ushort[] Esp32Vids =
    {
        0x10C4, // Silicon Labs CP210x
        0x1A86, // WCH CH340 / CH9102
        0x303A, // Espressif (S2/S3/C3 com USB nativo)
        0x0403, // FTDI
    };

    private static readonly string[] Esp32Keywords = { "CP210", "CH340", "CH910", "CH343", "SILICON LABS", "ESPRESSIF", "USB-SERIAL", "USB SERIAL", "USB-UART", "USB TO UART" };

    public bool LooksLikeEsp32
    {
        get
        {
            if (Vid != 0 && Array.IndexOf(Esp32Vids, Vid) >= 0) return true;
            string text = $"{Description} {HardwareId}".ToUpperInvariant();
            foreach (string keyword in Esp32Keywords)
                if (text.Contains(keyword, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    public override string ToString()
    {
        string description = string.IsNullOrWhiteSpace(Description) ? string.Empty : $" - {Description}";
        return LooksLikeEsp32 ? $"{PortName}{description} (provavel ESP32)" : $"{PortName}{description}";
    }
}

public interface ISerialPortScanner
{
    IReadOnlyList<SerialPortDescriptor> Scan();
}

/// <summary>
/// Enumeracao portavel via <see cref="SerialPort.GetPortNames"/>. No Windows
/// identifica o driver de cada porta pelo registro (o suficiente para achar o
/// CP210x ou o CH340 sem WMI); no Linux le o VID/PID do sysfs. A versao com nomes
/// amigaveis completos e' a WmiSerialPortScanner do projeto OledMirror.Windows.
/// </summary>
public sealed class BasicSerialPortScanner : ISerialPortScanner
{
    public IReadOnlyList<SerialPortDescriptor> Scan()
    {
        string[] names;
        try
        {
            names = SerialPort.GetPortNames();
        }
        catch (Exception)
        {
            return Array.Empty<SerialPortDescriptor>();
        }

        var ports = new List<SerialPortDescriptor>();
        Dictionary<string, string>? windowsDrivers = null;
        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SerialPortDescriptor port =
                OperatingSystem.IsLinux() ? DescribeLinux(name) :
                OperatingSystem.IsWindows() ? DescribeWindows(name, windowsDrivers ??= ReadWindowsDrivers()) :
                new SerialPortDescriptor(name, string.Empty);

            // No Linux, GetPortNames lista dezenas de /dev/ttyS* que nao existem de verdade.
            if (OperatingSystem.IsLinux() && port.Vid == 0 && name.StartsWith("/dev/ttyS", StringComparison.Ordinal)) continue;

            ports.Add(port);
        }

        return ports.OrderBy(p => p.LooksLikeEsp32 ? 0 : 1).ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>COMx -> nome do dispositivo do driver (ex.: \Device\Silabser0), de HKLM\HARDWARE\DEVICEMAP\SERIALCOMM.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Dictionary<string, string> ReadWindowsDrivers()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null) return map;
            foreach (string device in key.GetValueNames())
                if (key.GetValue(device) is string port) map[port.Trim('\0', ' ')] = device;
        }
        catch (Exception)
        {
            // Sem permissao de leitura: fica so com os nomes das portas.
        }
        return map;
    }

    private static SerialPortDescriptor DescribeWindows(string portName, Dictionary<string, string> drivers)
    {
        if (!drivers.TryGetValue(portName, out string? device)) return new SerialPortDescriptor(portName, string.Empty);

        string d = device.ToUpperInvariant();
        string description =
            d.Contains("SILABSER", StringComparison.Ordinal) ? "Silicon Labs CP210x" :
            d.Contains("WCH", StringComparison.Ordinal) || d.Contains("CH34", StringComparison.Ordinal) || d.Contains("CH910", StringComparison.Ordinal) ? "WCH CH340/CH9102" :
            d.Contains("USBSER", StringComparison.Ordinal) ? "USB serial (CDC)" :
            d.Contains("VCP", StringComparison.Ordinal) || d.Contains("FTDI", StringComparison.Ordinal) ? "FTDI USB serial" :
            d.Contains("SERIAL", StringComparison.Ordinal) && !d.Contains("USB", StringComparison.Ordinal) ? "porta serial da placa-mae" :
            string.Empty;

        return new SerialPortDescriptor(portName, description, device);
    }

    private static SerialPortDescriptor DescribeLinux(string portName)
    {
        try
        {
            string tty = Path.GetFileName(portName);
            string device = Path.GetFullPath(Path.Combine("/sys/class/tty", tty, "device"));
            // Sobe a arvore ate o dispositivo USB, que e' quem tem idVendor/idProduct.
            for (string? dir = device; dir is not null && dir.StartsWith("/sys", StringComparison.Ordinal); dir = Path.GetDirectoryName(dir))
            {
                string vidFile = Path.Combine(dir, "idVendor");
                if (!File.Exists(vidFile)) continue;

                ushort vid = Convert.ToUInt16(File.ReadAllText(vidFile).Trim(), 16);
                ushort pid = Convert.ToUInt16(File.ReadAllText(Path.Combine(dir, "idProduct")).Trim(), 16);
                string product = ReadOptional(Path.Combine(dir, "product"));
                string manufacturer = ReadOptional(Path.Combine(dir, "manufacturer"));
                string description = $"{manufacturer} {product}".Trim();
                return new SerialPortDescriptor(portName, description, $"USB VID_{vid:X4}&PID_{pid:X4}", vid, pid);
            }
        }
        catch (Exception)
        {
            // Sem sysfs (container, permissao): fica so com o nome.
        }
        return new SerialPortDescriptor(portName, string.Empty);
    }

    private static string ReadOptional(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
}
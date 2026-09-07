using System.Management;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using OledMirror.Core.Devices;

namespace OledMirror.Windows.Devices;

/// <summary>
/// Enumeracao de portas seriais no Windows com nome amigavel e VID/PID.
///
/// Isso importa porque a aplicacao nao deve depender de uma COM fixa: o Windows
/// atribui numeros diferentes conforme a porta USB usada. Com o VID/PID da' para
/// dizer "COM7 - Silicon Labs CP210x - provavel ESP32" e escolher sozinho.
///
/// Se o WMI falhar (servico desligado, politica), cai para a enumeracao basica:
/// a aplicacao continua utilizavel, so sem os nomes amigaveis.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiSerialPortScanner : ISerialPortScanner
{
    private static readonly Regex PortPattern = new(@"\((COM\d+)\)", RegexOptions.Compiled);
    private static readonly Regex VidPidPattern =
        new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly BasicSerialPortScanner _fallback = new();

    public IReadOnlyList<SerialPortDescriptor> Scan()
    {
        try
        {
            var found = new List<SerialPortDescriptor>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, PNPDeviceID, Caption FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");

            foreach (ManagementBaseObject item in searcher.Get())
            {
                string caption = item["Caption"]?.ToString() ?? string.Empty;
                Match portMatch = PortPattern.Match(caption);
                if (!portMatch.Success) continue;

                string portName = portMatch.Groups[1].Value;
                string hardwareId = item["PNPDeviceID"]?.ToString() ?? string.Empty;

                ushort vid = 0, pid = 0;
                Match ids = VidPidPattern.Match(hardwareId);
                if (ids.Success)
                {
                    vid = Convert.ToUInt16(ids.Groups[1].Value, 16);
                    pid = Convert.ToUInt16(ids.Groups[2].Value, 16);
                }

                // Remove o sufixo "(COMx)" da descricao: a porta ja e' uma coluna.
                string description = PortPattern.Replace(caption, string.Empty).Trim();

                found.Add(new SerialPortDescriptor(portName, description, hardwareId, vid, pid));
            }

            if (found.Count == 0) return _fallback.Scan();

            return found
                .GroupBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => PortNumber(p.PortName))
                .ToList();
        }
        catch (Exception)
        {
            // WMI indisponivel: a enumeracao basica ainda lista as portas.
            return _fallback.Scan();
        }
    }

    private static int PortNumber(string portName)
        => int.TryParse(portName.AsSpan(3), out int n) ? n : int.MaxValue;
}
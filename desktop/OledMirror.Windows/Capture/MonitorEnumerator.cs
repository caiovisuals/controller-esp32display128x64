using OledMirror.Core.Capture;

namespace OledMirror.Windows.Capture;

/// <summary>Um monitor conectado.</summary>
public sealed record MonitorInfo(string DeviceName, string DisplayName, int Left, int Top, int Width, int Height, bool IsPrimary)
{
    public override string ToString() => $"{DisplayName} - {Width}x{Height}";
}

/// <summary>Enumera monitores em coordenadas fisicas de pixel.</summary>
public static class MonitorEnumerator
{
    /// <summary>
    /// Declara o processo como Per-Monitor DPI Aware V2.
    /// Sem isso o Windows mente sobre as coordenadas para um processo nao-aware:
    /// um monitor 4K com escala de 150% seria reportado como 2560x1440 e a
    /// captura sairia esticada e borrada. Chame antes de qualquer captura.
    /// </summary>
    public static void EnableDpiAwareness()
    {
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(
                new IntPtr((int)NativeMethods.DPI_AWARENESS_CONTEXT.PerMonitorAwareV2));
        }
        catch (EntryPointNotFoundException)
        {
            // Windows anterior ao 10 1703. A aplicacao funciona, so nao fica
            // DPI-aware; a captura pode sair reescalada em telas com zoom.
        }
    }

    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var monitors = new List<MonitorInfo>();
        int index = 1;

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr handle, IntPtr _, ref NativeMethods.RECT _, IntPtr _) =>
            {
                var info = new NativeMethods.MONITORINFOEX
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                    szDevice = string.Empty,
                };

                if (NativeMethods.GetMonitorInfoW(handle, ref info))
                {
                    bool primary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                    string label = primary ? $"Monitor {index} (principal)" : $"Monitor {index}";

                    monitors.Add(new MonitorInfo(
                        info.szDevice, label,
                        info.rcMonitor.Left, info.rcMonitor.Top,
                        info.rcMonitor.Width, info.rcMonitor.Height,
                        primary));
                    index++;
                }
                return true;
            }, IntPtr.Zero);

        return monitors;
    }

    public static MonitorInfo? Find(string deviceName)
        => Enumerate().FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Retangulo que cobre todos os monitores.</summary>
    public static (int X, int Y, int Width, int Height) VirtualDesktopBounds()
    {
        IReadOnlyList<MonitorInfo> monitors = Enumerate();
        if (monitors.Count == 0) return (0, 0, 1920, 1080);

        int left = monitors.Min(m => m.Left);
        int top = monitors.Min(m => m.Top);
        int right = monitors.Max(m => m.Left + m.Width);
        int bottom = monitors.Max(m => m.Top + m.Height);
        return (left, top, right - left, bottom - top);
    }
}

/// <summary>Uma janela de nivel superior visivel.</summary>
public sealed record WindowInfo(IntPtr Handle, string Title, int Width, int Height)
{
    public override string ToString() => $"{Title} ({Width}x{Height})";
}

public static class WindowEnumerator
{
    /// <summary>Janelas visiveis, com titulo e tamanho utilizavel.</summary>
    public static IReadOnlyList<WindowInfo> Enumerate()
    {
        var windows = new List<WindowInfo>();

        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle)) return true;

            int length = NativeMethods.GetWindowTextLengthW(handle);
            if (length <= 0) return true;

            var buffer = new char[length + 1];
            int copied = NativeMethods.GetWindowTextW(handle, buffer, buffer.Length);
            if (copied <= 0) return true;

            string title = new(buffer, 0, copied);
            if (string.IsNullOrWhiteSpace(title)) return true;

            if (!NativeMethods.GetClientRect(handle, out NativeMethods.RECT rect)) return true;
            // Ignora janelas minusculas: sao quase sempre janelas auxiliares
            // invisiveis de outros processos, nao algo que o usuario queira espelhar.
            if (rect.Width < 64 || rect.Height < 64) return true;

            windows.Add(new WindowInfo(handle, title, rect.Width, rect.Height));
            return true;
        }, IntPtr.Zero);

        return windows.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static WindowInfo? Foreground()
    {
        IntPtr handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero) return null;
        return Enumerate().FirstOrDefault(w => w.Handle == handle);
    }
}

/// <summary>Implementacao Windows do provedor de fontes de captura.</summary>
public sealed class WindowsCaptureSourceProvider : ICaptureSourceProvider
{
    public IReadOnlyList<CaptureSourceDescriptor> EnumerateMonitors()
        => MonitorEnumerator.Enumerate()
            .Select(m => new CaptureSourceDescriptor(CaptureSourceKind.Monitor, m.DeviceName, m.DisplayName, m.Width, m.Height))
            .ToList();

    public IReadOnlyList<CaptureSourceDescriptor> EnumerateWindows()
        => WindowEnumerator.Enumerate()
            .Select(w => new CaptureSourceDescriptor(CaptureSourceKind.Window, w.Handle.ToString(), w.Title, w.Width, w.Height))
            .ToList();

    public ICaptureSource OpenMonitor(string id)
    {
        MonitorInfo monitor = MonitorEnumerator.Find(id)
            ?? MonitorEnumerator.Enumerate().FirstOrDefault(m => m.IsPrimary)
            ?? MonitorEnumerator.Enumerate().FirstOrDefault()
            ?? throw new CaptureException("Nenhum monitor encontrado.");

        return new MonitorCapture(monitor);
    }

    public ICaptureSource OpenWindow(string id)
    {
        if (!long.TryParse(id, out long raw)) throw new CaptureException($"Identificador de janela invalido: {id}");

        var handle = new IntPtr(raw);
        if (!NativeMethods.IsWindow(handle)) throw new CaptureException("A janela nao existe mais.");

        WindowInfo? info = WindowEnumerator.Enumerate().FirstOrDefault(w => w.Handle == handle);
        return new WindowCapture(handle, info?.Title ?? "janela");
    }

    public ICaptureSource OpenRegion(int x, int y, int width, int height) => new RegionCapture(x, y, width, height);
}
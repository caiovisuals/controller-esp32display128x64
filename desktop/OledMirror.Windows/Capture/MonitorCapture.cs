using OledMirror.Core.Capture;
using OledMirror.Core.Imaging;

namespace OledMirror.Windows.Capture;

/// <summary>
/// Captura um monitor inteiro via BitBlt do DC da tela.
///
/// Se o monitor for removido ou trocar de modo, a captura passa a devolver false
/// (sem excecao) ate a geometria voltar a ser valida - desconectar um monitor nao
/// pode derrubar a aplicacao.
/// </summary>
public sealed class MonitorCapture : ICaptureSource
{
    private readonly string _deviceName;
    private readonly GdiSurface _surface = new();
    private MonitorInfo _monitor;

    public MonitorCapture(MonitorInfo monitor)
    {
        _monitor = monitor;
        _deviceName = monitor.DeviceName;
        SourceWidth = monitor.Width;
        SourceHeight = monitor.Height;
    }

    public string Name => $"Monitor {_monitor.DisplayName}";
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }

    public bool TryCapture(out CapturedFrame frame)
    {
        frame = default;

        // Reconsulta a geometria: o usuario pode ter mudado a resolucao ou
        // rearranjado os monitores desde a ultima captura.
        MonitorInfo? current = MonitorEnumerator.Find(_deviceName);
        if (current is null) return false;

        _monitor = current;
        SourceWidth = current.Width;
        SourceHeight = current.Height;

        if (!_surface.Ensure(current.Width, current.Height)) return false;

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        try
        {
            // CAPTUREBLT inclui janelas em camadas (menus, tooltips), que fazem
            // parte do que o usuario esta vendo.
            bool ok = NativeMethods.BitBlt(
                _surface.MemoryDc, 0, 0, current.Width, current.Height,
                screenDc, current.Left, current.Top,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (!ok) return false;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        _surface.CopyToBuffer();
        frame = _surface.ToFrame();
        return true;
    }

    public void Dispose() => _surface.Dispose();
}

/// <summary>Captura um retangulo arbitrario da area de trabalho virtual.</summary>
public sealed class RegionCapture : ICaptureSource
{
    private readonly GdiSurface _surface = new();
    private readonly int _x, _y;

    public RegionCapture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Regiao precisa ter largura e altura positivas.");

        _x = x;
        _y = y;
        SourceWidth = width;
        SourceHeight = height;
    }

    public string Name => $"Regiao {SourceWidth}x{SourceHeight} em ({_x},{_y})";
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    public bool TryCapture(out CapturedFrame frame)
    {
        frame = default;
        if (!_surface.Ensure(SourceWidth, SourceHeight)) return false;

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return false;

        try
        {
            if (!NativeMethods.BitBlt(_surface.MemoryDc, 0, 0, SourceWidth, SourceHeight,
                                      screenDc, _x, _y,
                                      NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                return false;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        _surface.CopyToBuffer();
        frame = _surface.ToFrame();
        return true;
    }

    public void Dispose() => _surface.Dispose();
}
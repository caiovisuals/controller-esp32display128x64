using OledMirror.Core.Capture;
using OledMirror.Core.Imaging;

namespace OledMirror.Windows.Capture;

/// <summary>
/// Captura uma janela especifica.
///
/// Usa PrintWindow com PW_RENDERFULLCONTENT, que e' o unico caminho GDI que
/// funciona com janelas aceleradas por GPU (navegadores, Electron, UWP) e que
/// captura a janela mesmo parcialmente coberta. Se PrintWindow falhar - algumas
/// janelas simplesmente nao respondem a ele - cai para BitBlt do DC da janela.
///
/// Janela fechada ou minimizada devolve false, nao excecao: e' uma situacao
/// normal, e o pipeline apenas repete o ultimo frame.
/// </summary>
public sealed class WindowCapture : ICaptureSource
{
    private readonly IntPtr _handle;
    private readonly string _title;
    private readonly GdiSurface _surface = new();
    private bool _printWindowFailed;

    public WindowCapture(IntPtr handle, string title)
    {
        _handle = handle;
        _title = title;

        if (NativeMethods.GetClientRect(handle, out NativeMethods.RECT rect))
        {
            SourceWidth = Math.Max(1, rect.Width);
            SourceHeight = Math.Max(1, rect.Height);
        }
        else
        {
            SourceWidth = SourceHeight = 1;
        }
    }

    public string Name => $"Janela: {_title}";
    public int SourceWidth { get; private set; }
    public int SourceHeight { get; private set; }

    public bool TryCapture(out CapturedFrame frame)
    {
        frame = default;

        if (!NativeMethods.IsWindow(_handle)) return false;      // fechada
        if (NativeMethods.IsIconic(_handle)) return false;       // minimizada: nao ha o que capturar
        if (!NativeMethods.GetClientRect(_handle, out NativeMethods.RECT rect)) return false;

        int width = rect.Width;
        int height = rect.Height;
        if (width <= 0 || height <= 0) return false;

        SourceWidth = width;
        SourceHeight = height;

        if (!_surface.Ensure(width, height)) return false;

        bool captured = false;

        if (!_printWindowFailed)
        {
            captured = NativeMethods.PrintWindow(_handle, _surface.MemoryDc, NativeMethods.PW_RENDERFULLCONTENT);
            // Uma falha e' definitiva para esta janela: nao vale tentar de novo a
            // cada frame, o custo e' alto e o resultado nao muda.
            if (!captured) _printWindowFailed = true;
        }

        if (!captured)
        {
            IntPtr windowDc = NativeMethods.GetDC(_handle);
            if (windowDc == IntPtr.Zero) return false;
            try
            {
                captured = NativeMethods.BitBlt(_surface.MemoryDc, 0, 0, width, height,
                                                windowDc, 0, 0, NativeMethods.SRCCOPY);
            }
            finally
            {
                NativeMethods.ReleaseDC(_handle, windowDc);
            }
        }

        if (!captured) return false;

        _surface.CopyToBuffer();
        frame = _surface.ToFrame();
        return true;
    }

    public void Dispose() => _surface.Dispose();
}
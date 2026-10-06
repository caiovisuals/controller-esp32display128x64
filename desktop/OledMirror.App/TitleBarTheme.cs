using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OledMirror.App;

/// <summary>
/// Pinta a barra de titulo nativa com as cores do app, pedindo ao DWM em vez
/// de substituir a barra inteira (WindowChrome). Assim os botoes, o arraste e o
/// snap continuam sendo os do proprio Windows.
/// Windows 11: cor exata de fundo, texto e borda.
/// Windows 10 (1809+): so o modo escuro (barra preta), sem cor personalizada.
/// Versoes mais antigas ignoram as chamadas e ficam com a barra padrao.
/// </summary>
internal static class TitleBarTheme
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 1809-1909
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Aplica as cores assim que a janela tiver um handle nativo.</summary>
    public static void Apply(Window window, Color caption, Color text, Color border)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            ApplyNow(window, caption, text, border);
        else
            window.SourceInitialized += (_, _) => ApplyNow(window, caption, text, border);
    }

    private static void ApplyNow(Window window, Color caption, Color text, Color border)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;

        int dark = 1;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));

        Set(hwnd, DWMWA_CAPTION_COLOR, caption);
        Set(hwnd, DWMWA_TEXT_COLOR, text);
        Set(hwnd, DWMWA_BORDER_COLOR, border);
    }

    private static void Set(IntPtr hwnd, int attribute, Color color)
    {
        // COLORREF e' 0x00BBGGRR
        int colorRef = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(hwnd, attribute, ref colorRef, sizeof(int));
    }
}
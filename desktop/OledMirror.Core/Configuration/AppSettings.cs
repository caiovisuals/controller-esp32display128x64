using System.Text.Json;
using System.Text.Json.Serialization;
using OledMirror.Core.Capture;
using OledMirror.Core.Imaging;
using OledMirror.Core.Logging;
using OledMirror.Core.Pipeline;
using OledMirror.Core.Protocol;

namespace OledMirror.Core.Configuration;

/// <summary>O que vai para o painel: a tela espelhada ou um texto do usuario.</summary>
public enum ContentMode
{
    Mirror,
    Text,
}


/// <summary>Tudo o que a interface salva entre sessoes.</summary>
public sealed class AppSettings
{
    // Conexao
    public string? PortName { get; set; }
    public bool AutoDetectPort { get; set; } = true;
    public int BaudRate { get; set; } = 921600;
    public bool SimulateDevice { get; set; }
    public bool AutoReconnect { get; set; } = true;

    // Conteudo
    public ContentMode ContentMode { get; set; } = ContentMode.Mirror;
    public string DisplayText { get; set; } = "Ola, mundo!";
    /// <summary>0 = automatico (o maior que couber).</summary>
    public int TextScale { get; set; }
    public TextAlign TextAlign { get; set; } = TextAlign.Center;

    // Fonte
    public CaptureSourceKind SourceKind { get; set; } = CaptureSourceKind.Monitor;
    public string? SourceId { get; set; }
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; } = 640;
    public int RegionHeight { get; set; } = 320;

    // Imagem
    public ResizeMode ResizeMode { get; set; } = ResizeMode.Fit;
    public DitheringMode Dithering { get; set; } = DitheringMode.FloydSteinberg;
    public int Threshold { get; set; } = 128;
    public double Contrast { get; set; } = 1.0;
    public double Gamma { get; set; } = 1.0;
    public bool AutoContrast { get; set; } = true;
    public bool Invert { get; set; }

    // Transmissao
    public int TargetFps { get; set; } = 10;
    public FrameEncoding Encoding { get; set; } = FrameEncoding.Auto;
    public bool SkipUnchangedFrames { get; set; } = true;
    public bool PreviewOnly { get; set; }

    // Painel
    public int DisplayContrast { get; set; } = 127;
    public bool DisplayInvert { get; set; }

    // Diagnostico
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
    public bool LogToFile { get; set; } = true;

    public TextOptions ToTextOptions() => new()
    {
        Text = DisplayText ?? string.Empty,
        Scale = TextScale,
        Align = TextAlign,
    };

    public ImageProcessorOptions ToImageOptions() => new()
    {
        ResizeMode = ResizeMode,
        Dithering = Dithering,
        Threshold = Math.Clamp(Threshold, 0, 255),
        Contrast = Contrast,
        Gamma = Gamma,
        AutoContrast = AutoContrast,
        Invert = Invert,
    };

    public ImageProcessorOptions ToTextImageOptions() => new()
    {
        ResizeMode = ResizeMode.Stretch,
        Dithering = DitheringMode.Threshold,
        Threshold = 128,
        AutoContrast = false,
        Invert = Invert,
    };

    public MirrorSettings ToMirrorSettings() => new()
    {
        TargetFps = TargetFps,
        Encoding = Encoding,
        SkipUnchangedFrames = SkipUnchangedFrames,
        PreviewOnly = PreviewOnly,
        Image = ContentMode == ContentMode.Text ? ToTextImageOptions() : ToImageOptions(),
    };

    /// <summary>Corrige valores fora da faixa (arquivo editado a mao, versao antiga).</summary>
    public void Normalize()
    {
        if (BaudRate <= 0) BaudRate = 921600;
        if (!MirrorSettings.SupportedFps.Contains(TargetFps)) TargetFps = 10;
        Threshold = Math.Clamp(Threshold, 0, 255);
        DisplayContrast = Math.Clamp(DisplayContrast, 0, 255);
        RegionWidth = Math.Max(16, RegionWidth);
        RegionHeight = Math.Max(16, RegionHeight);
        if (Contrast <= 0 || double.IsNaN(Contrast)) Contrast = 1.0;
        if (Gamma <= 0 || double.IsNaN(Gamma)) Gamma = 1.0;
        DisplayText ??= string.Empty;
        TextScale = Math.Clamp(TextScale, 0, TextOptions.MaxScale);
    }
}

/// <summary>Le e grava <see cref="AppSettings"/> em JSON (%APPDATA%\OledMirror\settings.json).</summary>
public sealed class SettingsStore
{
    private const string Cat = "config";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Logger _log;

    public SettingsStore(string? path = null, Logger? log = null)
    {
        Path = path ?? System.IO.Path.Combine(DataDirectory(), "settings.json");
        _log = log ?? Logger.Null;
    }

    public string Path { get; }

    public static string DataDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root)) root = AppContext.BaseDirectory;
        return System.IO.Path.Combine(root, "OledMirror");
    }

    public static string DefaultLogPath()
        => System.IO.Path.Combine(DataDirectory(), "logs", $"oledmirror-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Carrega; arquivo ausente ou invalido devolve os padroes.</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Json);
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Warning(Cat, $"Configuracao invalida em {Path} ({ex.Message}); usando os padroes.");
        }
        return new AppSettings();
    }

    public bool Save(AppSettings settings)
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Grava num temporario e troca: um encerramento no meio nao corrompe o arquivo.
            string temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Warning(Cat, $"Nao foi possivel salvar a configuracao: {ex.Message}");
            return false;
        }
    }
}
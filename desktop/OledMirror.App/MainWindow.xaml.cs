using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OledMirror.Core.Imaging;
using OledMirror.Windows.App;

namespace OledMirror.App;

/// <summary>
/// Codigo por tras da janela. Deliberadamente minimo: toda a logica vive na
/// <see cref="MainViewModel"/>, que nao depende de WPF. Aqui so ficam as duas
/// coisas que precisam mesmo de WPF - desenhar a previa num WriteableBitmap e
/// rolar o log automaticamente - e o dialogo de abrir arquivo do modo texto.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly WriteableBitmap _preview;
    private readonly byte[] _previewPixels = new byte[DisplayGeometry.PixelCount];
    private bool _logScrollPending;

    public MainWindow()
    {
        InitializeComponent();

        TitleBarTheme.Apply(this,
            caption: ((SolidColorBrush)FindResource("Bg")).Color,
            text: ((SolidColorBrush)FindResource("Text")).Color,
            border: ((SolidColorBrush)FindResource("Border")).Color);

        var app = (App)Application.Current;
        _viewModel = new MainViewModel(app.Log, app.LogSink, app.SettingsStore);
        DataContext = _viewModel;

        // Gray8: 1 byte por pixel, que e' exatamente o que MonoFrameUtils.Unpack
        // produz. Sem conversao de formato no caminho da previa.
        _preview = new WriteableBitmap(DisplayGeometry.Width, DisplayGeometry.Height, 96, 96,
                                       PixelFormats.Gray8, null);
        PreviewImage.Source = _preview;

        _viewModel.PreviewUpdated += OnPreviewUpdated;
        _viewModel.LogEntries.CollectionChanged += OnLogEntriesChanged;

        Closed += (_, _) =>
        {
            _viewModel.PreviewUpdated -= OnPreviewUpdated;
            _viewModel.Dispose();
        };

        RenderPreview();
    }

    private void OnPreviewUpdated() => RenderPreview();

    private void RenderPreview()
    {
        lock (_viewModel.PreviewFrame)
        {
            MonoFrameUtils.Unpack(_viewModel.PreviewFrame, _previewPixels);
        }

        _preview.WritePixels(
            new Int32Rect(0, 0, DisplayGeometry.Width, DisplayGeometry.Height),
            _previewPixels, DisplayGeometry.Width, 0);
    }

    private void OnOpenTextFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Abrir texto para o painel",
            Filter = "Texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            _viewModel.DisplayText = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Nao foi possivel ler o arquivo:\n{ex.Message}", "OledMirror",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _logScrollPending) return;

        // Nao rolar dentro do proprio CollectionChanged: este handler pode rodar
        // antes de o ListBox processar o mesmo evento, e o ScrollIntoView forca um
        // layout com o gerador de itens desatualizado ("ItemsControl inconsistente").
        // Adiado, roda depois de todos os handlers, uma vez por rajada de linhas.
        _logScrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _logScrollPending = false;
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        });
    }
}
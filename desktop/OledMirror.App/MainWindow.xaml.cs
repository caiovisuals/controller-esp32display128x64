using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OledMirror.Core.Imaging;
using OledMirror.Windows.App;

namespace OledMirror.App;

/// <summary>
/// Codigo por tras da janela. Deliberadamente minimo: toda a logica vive na
/// <see cref="MainViewModel"/>, que nao depende de WPF. Aqui so ficam as duas
/// coisas que precisam mesmo de WPF - desenhar a previa num WriteableBitmap e
/// rolar o log automaticamente.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly WriteableBitmap _preview;
    private readonly byte[] _previewPixels = new byte[DisplayGeometry.PixelCount];

    public MainWindow()
    {
        InitializeComponent();

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

    private void OnLogEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
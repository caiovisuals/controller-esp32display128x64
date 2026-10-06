using OledMirror.Core.Capture;
using OledMirror.Core.Configuration;
using OledMirror.Core.Imaging;

namespace OledMirror.Tests.Capture;

public class TextSourceTests
{
    private static byte[] Render(TextOptions options, double time = 0, bool invert = false)
    {
        var settings = new AppSettings { ContentMode = ContentMode.Text, Invert = invert };
        using var source = new TextSource(options) { FixedTimeSeconds = time };
        Assert.True(source.TryCapture(out CapturedFrame captured));
        var frame = new byte[DisplayGeometry.FrameBytes];
        new FrameProcessor(settings.ToMirrorSettings().Image).Process(captured, frame);
        return frame;
    }

    private static (int MinX, int MaxX, int MinY, int MaxY) LitBounds(byte[] frame)
    {
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = 0; y < DisplayGeometry.Height; y++)
            for (int x = 0; x < DisplayGeometry.Width; x++)
                if ((frame[DisplayGeometry.ByteIndex(x, y)] & DisplayGeometry.BitMask(y)) != 0)
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        return (minX, maxX, minY, maxY);
    }

    [Fact]
    public void Source_IsNative128x64()
    {
        using var source = new TextSource(new TextOptions { Text = "OI" });
        Assert.Equal(DisplayGeometry.Width, source.SourceWidth);
        Assert.Equal(DisplayGeometry.Height, source.SourceHeight);
    }

    [Fact]
    public void EmptyText_LeavesThePanelDark()
    {
        Assert.Equal(0, MonoFrameUtils.CountLitPixels(Render(new TextOptions { Text = "  \n\n" })));
    }

    [Fact]
    public void AutoScale_PicksTheLargestThatFits()
    {
        using var shortText = new TextSource(new TextOptions { Text = "OLA" });
        Assert.Equal(TextOptions.MaxScale, shortText.Scale);
        Assert.False(shortText.Scrolls);

        using var longText = new TextSource(new TextOptions { Text = "uma frase bem mais comprida que nao cabe grande" });
        Assert.True(longText.Scale < TextOptions.MaxScale);
        Assert.False(longText.Scrolls);
    }

    [Fact]
    public void Wrap_BreaksOnWordsAndKeepsLinesWithinTheWidth()
    {
        using var source = new TextSource(new TextOptions { Text = "abc defgh ijklmnopqrstuvwxyz0123456789 fim", Scale = 1 });
        Assert.All(source.Lines, line => Assert.True(line.Length <= 21, line));
        Assert.Equal("abc defgh", source.Lines[0]);
        Assert.Equal("ijklmnopqrstuvwxyz012", source.Lines[1]);
        Assert.Equal("3456789 fim", source.Lines[2]);
    }

    [Fact]
    public void Newlines_StartNewLines()
    {
        using var source = new TextSource(new TextOptions { Text = "linha 1\r\nlinha 2\n\nlinha 4", Scale = 1 });
        Assert.Equal(new[] { "linha 1", "linha 2", "", "linha 4" }, source.Lines);
    }

    [Fact]
    public void Text_IsCenteredByDefault()
    {
        (int minX, int maxX, int minY, int maxY) = LitBounds(Render(new TextOptions { Text = "HM", Scale = 2 }));
        Assert.InRange(minX + maxX, 126, 128);
        Assert.InRange(minY + maxY, 62, 64);
    }

    [Fact]
    public void Alignment_MovesTheText()
    {
        Assert.Equal(0, LitBounds(Render(new TextOptions { Text = "HM", Scale = 1, Align = TextAlign.Left })).MinX);
        Assert.Equal(127, LitBounds(Render(new TextOptions { Text = "HM", Scale = 1, Align = TextAlign.Right })).MaxX);
    }

    [Fact]
    public void Accents_FoldToTheBaseLetter()
    {
        byte[] accented = Render(new TextOptions { Text = "ação é útil", Scale = 1 });
        byte[] plain = Render(new TextOptions { Text = "acao e util", Scale = 1 });
        Assert.Equal(plain, accented);
    }

    [Fact]
    public void LowercaseAndUppercase_AreDifferentGlyphs()
    {
        Assert.NotEqual(Render(new TextOptions { Text = "abc", Scale = 1 }), Render(new TextOptions { Text = "ABC", Scale = 1 }));
    }

    [Fact]
    public void TextTooLong_Scrolls()
    {
        string text = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"linha {i}"));
        using var source = new TextSource(new TextOptions { Text = text });
        Assert.Equal(1, source.Scale);
        Assert.True(source.Scrolls);

        Assert.NotEqual(Render(new TextOptions { Text = text }, time: 0), Render(new TextOptions { Text = text }, time: 2));
    }

    [Fact]
    public void StaticText_DoesNotDependOnTime()
    {
        Assert.Equal(Render(new TextOptions { Text = "fixo" }, time: 0), Render(new TextOptions { Text = "fixo" }, time: 5));
    }

    [Fact]
    public void Update_ChangesTheNextCapture()
    {
        using var source = new TextSource(new TextOptions { Text = "antes" }) { FixedTimeSeconds = 0 };
        source.TryCapture(out CapturedFrame first);
        byte[] before = first.Buffer.ToArray();

        source.Update(new TextOptions { Text = "depois" });
        source.TryCapture(out CapturedFrame second);

        Assert.NotEqual(before, second.Buffer);
        Assert.Equal("depois", source.Options.Text);
    }

    [Fact]
    public void Invert_LightsTheBackground()
    {
        byte[] normal = Render(new TextOptions { Text = "OI" });
        byte[] inverted = Render(new TextOptions { Text = "OI" }, invert: true);
        Assert.Equal(DisplayGeometry.PixelCount, MonoFrameUtils.CountLitPixels(normal) + MonoFrameUtils.CountLitPixels(inverted));
    }

    [Fact]
    public void Settings_TextModeSkipsImageAdjustments()
    {
        var settings = new AppSettings
        {
            ContentMode = ContentMode.Text,
            Dithering = DitheringMode.FloydSteinberg,
            AutoContrast = true,
            Gamma = 2.0,
        };
        ImageProcessorOptions image = settings.ToMirrorSettings().Image;
        Assert.Equal(DitheringMode.Threshold, image.Dithering);
        Assert.False(image.AutoContrast);
        Assert.Equal(1.0, image.Gamma);

        settings.ContentMode = ContentMode.Mirror;
        Assert.Equal(DitheringMode.FloydSteinberg, settings.ToMirrorSettings().Image.Dithering);
    }
}
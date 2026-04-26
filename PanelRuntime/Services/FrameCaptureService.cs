using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;

namespace PanelRuntime.Services;

public sealed class FrameCaptureService
{
    private readonly WebView2 _webView;
    private readonly int _width;
    private readonly int _height;

    public FrameCaptureService(WebView2 webView, int width, int height)
    {
        _webView = webView;
        _width = width;
        _height = height;
    }

    public async Task<(byte[] Bgra, double ElapsedMs)> CaptureBgraAsync()
    {
        var sw = Stopwatch.StartNew();
        await _webView.ExecuteScriptAsync("new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");
        await using var png = new MemoryStream();
        await _webView.CoreWebView2.CapturePreviewAsync(
            Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,
            png);
        png.Position = 0;

        var decoder = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        BitmapSource source = frame;
        if (frame.PixelWidth != _width || frame.PixelHeight != _height)
        {
            var scaleX = (double)_width / frame.PixelWidth;
            var scaleY = (double)_height / frame.PixelHeight;
            source = new TransformedBitmap(frame, new ScaleTransform(scaleX, scaleY));
        }

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = _width * 4;
        var pixels = new byte[stride * _height];
        converted.CopyPixels(pixels, stride, 0);
        sw.Stop();
        return (pixels, sw.Elapsed.TotalMilliseconds);
    }
}

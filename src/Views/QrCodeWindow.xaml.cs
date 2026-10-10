using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace CloCloWidget;

// A QR code for whatever text/URL was selected — the fastest way to get a
// link onto your phone without any companion-app pairing, since the
// phone's own camera just scans it. Generated entirely locally via
// QRCoder, same "no network call" spirit as the rest of this widget.
public partial class QrCodeWindow : Window
{
    public QrCodeWindow(string text)
    {
        InitializeComponent();
        SourceTextBox.Text = text;

        byte[] png;
        using (var generator = new QRCodeGenerator())
        using (var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q))
        using (var qrCode = new PngByteQRCode(data))
            png = qrCode.GetGraphic(10);

        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(png))
        {
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
        }
        bitmap.Freeze();
        QrImage.Source = bitmap;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyButton.Content = SafeClipboard.TrySetText(SourceTextBox.Text) ? "Copied!" : "Clipboard busy";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

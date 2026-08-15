using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace DeepSeekHarnessDesktop;

public partial class ImagePreviewWindow : Window
{
    private double _scale = 1;

    public ImagePreviewWindow(Window owner, string title, string source)
    {
        InitializeComponent();
        Owner = owner;
        PreviewTitle.Text = title + " · 项目预览";
        PreviewImage.Source = new BitmapImage(new Uri(source, UriKind.RelativeOrAbsolute));
        ContentRendered += (_, _) => FitImage();
    }

    private void SetScale(double value)
    {
        _scale = Math.Clamp(value, 0.2, 4);
        ImageScale.ScaleX = ImageScale.ScaleY = _scale;
        ZoomText.Text = $"{_scale * 100:0}%";
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => SetScale(_scale + 0.15);
    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => SetScale(_scale - 0.15);
    private void FitImage()
    {
        if (PreviewImage.Source is not BitmapSource bitmap || bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) return;
        var width = Math.Max(100, ImageScroll.ViewportWidth - 24);
        var height = Math.Max(100, ImageScroll.ViewportHeight - 24);
        SetScale(Math.Min(1, Math.Min(width / bitmap.Width, height / bitmap.Height)));
    }
    private void FitButton_Click(object sender, RoutedEventArgs e) => FitImage();
    private void ImageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetScale(_scale + (e.Delta > 0 ? 0.1 : -0.1));
        e.Handled = true;
    }
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

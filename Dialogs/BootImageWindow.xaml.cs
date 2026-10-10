using System.Windows;
using System.Windows.Media.Imaging;

namespace GreenLuma_Manager.Dialogs;

/// <summary>
/// Borderless splash that displays the boot image while a GreenLuma launch is in
/// progress. The image is centered on the primary display.
/// </summary>
public partial class BootImageWindow : Window
{
    // The splash is capped at a quarter of the primary display in each dimension
    // so it stays small and out of the way of Steam and any error dialogs.
    private const double MaxScreenFraction = 0.25;

    public BootImageWindow(BitmapSource image)
    {
        InitializeComponent();
        ApplyImage(image);
    }

    private void ApplyImage(BitmapSource image)
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;

        var maxWidth = screenWidth * MaxScreenFraction;
        var maxHeight = screenHeight * MaxScreenFraction;

        double width = image.PixelWidth;
        double height = image.PixelHeight;

        var scale = Math.Min(1.0, Math.Min(maxWidth / width, maxHeight / height));
        width *= scale;
        height *= scale;

        BootImage.Source = image;
        BootImage.Width = width;
        BootImage.Height = height;

        Width = width;
        Height = height;
        Left = (screenWidth - width) / 2;
        Top = (screenHeight - height) / 2;
    }
}

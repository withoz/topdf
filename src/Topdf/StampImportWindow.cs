using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Topdf;

internal sealed class StampImportWindow : Window
{
    readonly BitmapSource original;
    readonly BitmapSource thumbnail;
    readonly Image preview = new() { Stretch = Stretch.Uniform };
    readonly CheckBox remove = new() { Content = "배경 제거", Margin = new Thickness(0, 0, 0, 8) };
    readonly Slider strength = new() { Minimum = 0, Maximum = 120, Value = 30, TickFrequency = 1, IsSnapToTickEnabled = true };
    readonly TextBlock strengthLabel = new();
    readonly Border sample = new() { Width = 18, Height = 18, BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(1) };
    Color background;

    internal StampImportWindow(BitmapSource image, Window? owner)
    {
        original = image;
        thumbnail = new FormatConvertedBitmap(ScaledPreview(image), PixelFormats.Bgra32, null, 0);
        background = DetectBackground(thumbnail, out var hasTransparency);
        Title = "도장 가져오기";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        if (owner != null) Owner = owner;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;

        var layout = new StackPanel { Margin = new Thickness(16) };
        Content = layout;
        var backdrop = new Border { Height = 250, Background = Checkerboard(), Child = preview,
            BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12) };
        layout.Children.Add(backdrop);
        layout.Children.Add(remove);
        var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        colorRow.Children.Add(sample);
        colorRow.Children.Add(new TextBlock { Text = "미리보기에서 제거할 배경색을 클릭하세요.", Margin = new Thickness(8, 0, 0, 0) });
        layout.Children.Add(colorRow);
        layout.Children.Add(strengthLabel);
        layout.Children.Add(strength);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var apply = new Button { Content = "가져오기", IsDefault = true, MinWidth = 90, Padding = new Thickness(8, 4, 8, 4) };
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(apply);
        buttons.Children.Add(new Button { Content = "취소", IsCancel = true, MinWidth = 64, Margin = new Thickness(8, 0, 0, 0) });
        layout.Children.Add(buttons);

        remove.IsChecked = !hasTransparency;
        remove.Checked += (_, _) => RefreshPreview();
        remove.Unchecked += (_, _) => RefreshPreview();
        strength.ValueChanged += (_, _) => RefreshPreview();
        preview.MouseLeftButtonDown += (_, e) => {
            var scale = Math.Min(preview.ActualWidth / thumbnail.PixelWidth, preview.ActualHeight / thumbnail.PixelHeight);
            if (scale <= 0) return;
            var point = e.GetPosition(preview);
            var x = (int)Math.Floor((point.X - (preview.ActualWidth - thumbnail.PixelWidth * scale) / 2) / scale);
            var y = (int)Math.Floor((point.Y - (preview.ActualHeight - thumbnail.PixelHeight * scale) / 2) / scale);
            if (x < 0 || y < 0 || x >= thumbnail.PixelWidth || y >= thumbnail.PixelHeight) return;
            var color = ReadPixel(thumbnail, x, y);
            if (color.A == 0) return;
            background = color;
            remove.IsChecked = true;
            RefreshPreview();
        };
        RefreshPreview();
    }

    internal bool RemoveBackground => remove.IsChecked == true;
    internal Color BackgroundColor => background;
    internal int Tolerance => (int)strength.Value;

    void RefreshPreview()
    {
        strength.IsEnabled = RemoveBackground;
        strengthLabel.Text = $"제거 강도 {Tolerance}";
        sample.Background = new SolidColorBrush(background);
        preview.Source = RemoveBackground ? RemoveEdgeBackground(thumbnail, background, Tolerance) : thumbnail;
    }

    static BitmapSource ScaledPreview(BitmapSource image)
    {
        var factor = 460.0 / Math.Max(image.PixelWidth, image.PixelHeight);
        return factor >= 1 ? image : new TransformedBitmap(image, new ScaleTransform(factor, factor));
    }

    static Brush Checkerboard()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(Brushes.Gainsboro, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(Brushes.Gainsboro, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        return new DrawingBrush(group) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16) };
    }

    static Color ReadPixel(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    static Color DetectBackground(BitmapSource image, out bool transparent)
    {
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        transparent = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] < 255) { transparent = true; break; }

        var right = image.PixelWidth - 1;
        var bottom = image.PixelHeight - 1;
        var corners = new[] { ReadPixel(image, 0, 0), ReadPixel(image, right, 0), ReadPixel(image, 0, bottom), ReadPixel(image, right, bottom) };
        return corners.Where(c => c.A > 0).OrderBy(c =>
            corners.Sum(other => Math.Abs(c.R - other.R) + Math.Abs(c.G - other.G) + Math.Abs(c.B - other.B)))
            .FirstOrDefault(Colors.White);
    }

    internal static BitmapSource RemoveEdgeBackground(BitmapSource source, Color background, int tolerance)
    {
        var image = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var pixels = new byte[checked(width * height * 4)];
        image.CopyPixels(pixels, width * 4, 0);
        var frontier = new Queue<int>();

        void Visit(int x, int y)
        {
            var index = y * width + x;
            var offset = index * 4;
            if (pixels[offset + 3] == 0 ||
                Math.Abs(pixels[offset] - background.B) > tolerance ||
                Math.Abs(pixels[offset + 1] - background.G) > tolerance ||
                Math.Abs(pixels[offset + 2] - background.R) > tolerance) return;
            pixels[offset + 3] = 0;
            frontier.Enqueue(index);
        }

        for (var x = 0; x < width; x++) { Visit(x, 0); Visit(x, height - 1); }
        for (var y = 0; y < height; y++) { Visit(0, y); Visit(width - 1, y); }
        while (frontier.TryDequeue(out var index))
        {
            var x = index % width;
            var y = index / width;
            if (x > 0) Visit(x - 1, y);
            if (x + 1 < width) Visit(x + 1, y);
            if (y > 0) Visit(x, y - 1);
            if (y + 1 < height) Visit(x, y + 1);
        }
        var result = BitmapSource.Create(width, height, image.DpiX, image.DpiY, PixelFormats.Bgra32, null, pixels, width * 4);
        result.Freeze();
        return result;
    }
}

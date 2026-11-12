using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Topdf;

public partial class MainWindow
{
    async void ExportOtherFormat(object sender, RoutedEventArgs e)
    {
        if (exporting || sender is not MenuItem { Tag: string format } ||
            SelectedJob is not { Pdf: not null } job || pdfEditor is not { } editor || editor.IsBusy)
            return;

        var (pageCount, currentPage) = editor.ExportPagePosition;
        if (pageCount == 0) return;
        var panel = new StackPanel { Margin = new Thickness(16) };
        var options = new Window {
            Owner = this, Title = $"{format.ToUpperInvariant()} 내보내기",
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 375,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Content = panel
        };
        panel.Children.Add(new TextBlock { Text = $"내보낼 쪽 · 전체 {pageCount}쪽" });
        var scope = new ComboBox { ItemsSource = new[] { "전체 쪽", $"현재 쪽 ({currentPage})", "쪽 범위 지정" }, SelectedIndex = 0 };
        panel.Children.Add(scope);
        var range = new TextBox { ToolTip = "예: 1,3-5", Visibility = Visibility.Collapsed };
        panel.Children.Add(range);
        scope.SelectionChanged += (_, _) => range.Visibility = scope.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        var isDocx = format == "docx";
        var quality = new ComboBox {
            Margin = new Thickness(0, 8, 0, 0),
            ItemsSource = isDocx ? new[] { "편집 가능한 글자", "페이지 모양 유지 (이미지)" } : new[] { "96 DPI", "150 DPI", "300 DPI" },
            SelectedIndex = isDocx ? 0 : 1
        };
        panel.Children.Add(quality);
        panel.Children.Add(new TextBlock {
            Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = isDocx ? "글자 방식은 추출한 본문을 편집할 수 있습니다. 원본 배치가 중요하면 페이지 모양 유지를 선택하세요."
                : "여러 쪽을 내보내면 파일 이름 뒤에 쪽 번호를 붙여 각각 저장합니다."
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(buttons);
        var next = new Button { Content = "저장 위치 선택", IsDefault = true, Padding = new Thickness(8, 3, 8, 3) };
        buttons.Children.Add(next);
        buttons.Children.Add(new Button { Content = "취소", IsCancel = true, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) });
        string pages = "";
        next.Click += (_, _) => {
            pages = scope.SelectedIndex switch { 1 => currentPage.ToString(), 2 => range.Text.Replace(" ", ""), _ => "" };
            if (scope.SelectedIndex == 2 && !ValidExportRange(pages, pageCount)) {
                MessageBox.Show(options, $"1~{pageCount} 사이의 쪽 번호를 입력하세요. 예: 1,3-5", options.Title);
                range.Focus();
                return;
            }
            options.DialogResult = true;
        };
        if (options.ShowDialog() != true) return;
        var save = new SaveFileDialog {
            Title = options.Title, Filter = $"{format.ToUpperInvariant()} (*.{format})|*.{format}",
            DefaultExt = format, AddExtension = true, FileName = Path.GetFileNameWithoutExtension(job.Name)
        };
        if (save.ShowDialog(this) != true) return;

        exporting = true;
        try {
            Summary.Text = $"{format.ToUpperInvariant()} 내보내는 중…";
            var source = await GetPdfForOutputAsync(job);
            var dpi = isDocx ? 150 : new[] { 96, 150, 300 }[quality.SelectedIndex];
            var count = await PdfEditService.ExportFormatAsync(source, save.FileName, format, pages, dpi, isDocx && quality.SelectedIndex == 1, lifetime.Token);
            Summary.Text = $"{format.ToUpperInvariant()} 내보내기 완료 · {count}개 파일";
        } catch (OperationCanceledException) {
            Summary.Text = "내보내기를 취소했습니다.";
        } catch (Exception error) {
            Summary.Text = "내보내기 실패";
            MessageBox.Show(this, error.Message, options.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        } finally {
            exporting = false;
        }
    }

    static bool ValidExportRange(string range, int count)
    {
        foreach (var token in range.Split(',')) {
            var ends = token.Split('-');
            if (ends.Length > 2 || !int.TryParse(ends[0], out var first) || first < 1 || first > count)
                return false;
            if (ends.Length == 2 && (!int.TryParse(ends[1], out var last) || last < first || last > count))
                return false;
        }
        return true;
    }
}

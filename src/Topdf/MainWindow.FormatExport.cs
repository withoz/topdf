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
        var hasLayoutMode = format is "docx" or "xlsx";
        var quality = new ComboBox {
            Margin = new Thickness(0, 8, 0, 0),
            ItemsSource = hasLayoutMode
                ? new[] { "페이지 모양 유지 (이미지)", format == "docx" ? "편집 가능한 글자" : "편집 가능한 셀" }
                : new[] { "96 DPI", "150 DPI", "300 DPI" },
            SelectedIndex = format == "docx" ? 0 : 1
        };
        var hasDpi = format is "png" or "jpg" or "pptx";
        if (hasLayoutMode || hasDpi) panel.Children.Add(quality);
        var cadScaleInput = new TextBox { Text = "1" };
        if (format == "dxf") {
            panel.Children.Add(new TextBlock { Text = "도면 축척 1:N", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(cadScaleInput);
        }
        panel.Children.Add(new TextBlock {
            Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
            Text = format switch {
                "docx" => "페이지 모양 유지는 표, 색상, 그림과 여백을 한 쪽 이미지로 저장합니다. 편집 가능한 글자를 선택하면 본문을 추출합니다.",
                "hwpx" => "PDF 본문을 편집 가능한 글자로 옮깁니다. 원본 표와 그림, 배치는 재현하지 않습니다.",
                "xlsx" => "편집 가능한 셀은 PDF의 표 선과 색을 읽어 테두리, 채움, 병합을 복원합니다. 페이지 모양 유지는 쪽 이미지를 시트마다 넣습니다.",
                "pptx" => "PDF 한 쪽을 슬라이드 이미지 한 장으로 넣습니다.",
                "dxf" => "벡터 선·곡선·문자와 다각형 단색 채움을 mm 단위로 저장합니다. N=1은 PDF 용지 크기입니다. 실제 도면 치수는 원본 축척을 확인해 입력하세요. 이미지 속 선은 제외됩니다.",
                _ => "여러 쪽을 내보내면 파일 이름 뒤에 쪽 번호를 붙여 각각 저장합니다."
            }
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(buttons);
        var next = new Button { Content = "저장 위치 선택", IsDefault = true, Padding = new Thickness(8, 3, 8, 3) };
        buttons.Children.Add(next);
        buttons.Children.Add(new Button { Content = "취소", IsCancel = true, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) });
        string pages = "";
        double cadScale = 1;
        next.Click += (_, _) => {
            pages = scope.SelectedIndex switch { 1 => currentPage.ToString(), 2 => range.Text.Replace(" ", ""), _ => "" };
            if (scope.SelectedIndex == 2 && !ValidExportRange(pages, pageCount)) {
                MessageBox.Show(options, $"1~{pageCount} 사이의 쪽 번호를 입력하세요. 예: 1,3-5", options.Title);
                range.Focus();
                return;
            }
            if (format == "dxf" && (!double.TryParse(cadScaleInput.Text, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.CurrentCulture, out cadScale) || !(cadScale >= 1 && cadScale <= 1000000))) {
                MessageBox.Show(options, "축척의 N 값은 1부터 1000000까지 입력하세요.", options.Title);
                cadScaleInput.Focus();
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
            var dpi = hasDpi ? new[] { 96, 150, 300 }[quality.SelectedIndex] : 150;
            var count = await PdfEditService.ExportFormatAsync(source, save.FileName, format, pages, dpi, hasLayoutMode && quality.SelectedIndex == 0, lifetime.Token, cadScale);
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

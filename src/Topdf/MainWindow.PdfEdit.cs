using System.IO;
using System.Windows;

namespace Topdf;
public partial class MainWindow
{
    async void EditPdf(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { Pdf: not null } job) { Summary.Text = "PDF 또는 변환이 완료된 문서를 먼저 선택하세요."; return; }
        var editor = new PdfEditorWindow(job.Pdf, Path.GetFileName(job.Source)) { Owner = this };
        editor.ShowDialog();
        if (!closed && editor.ResultPath != null) {
            await OpenFilesAsync([editor.ResultPath]);
            Summary.Text = "수정본을 새 탭으로 열었습니다. 원본은 보존했습니다.";
        }
    }
}

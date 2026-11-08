using System.IO;
using System.Windows;

namespace Topdf;
public partial class MainWindow
{
    PdfEditorView? pdfEditor;
    public PdfEditorView? ActivePdfEditor => pdfEditor;
    async void EditPdf(object sender, RoutedEventArgs e)
    {
        if (pdfEditor != null) {
            if (!pdfEditor.CanClose()) return;
            EndPdfEditing(); await ShowPreviewAsync(SelectedJob); return;
        }
        if (SelectedJob is not { Pdf: not null } job) { Summary.Text = "PDF 또는 변환이 완료된 문서를 먼저 선택하세요."; return; }
        await StartPdfEditorAsync(job.Pdf,Path.GetFileName(job.Source));
    }
    async Task StartPdfEditorAsync(string source,string name)
    {
        if(pdfEditor!=null){if(!pdfEditor.CanClose())return;EndPdfEditing();}
        SetMenuOpen(false); SmartToggle.IsChecked = false; previewVersion++;
        pdfEditor = new PdfEditorView(source, name);
        pdfEditor.Saved += async (_,_) => {
            var result = pdfEditor?.ResultPath;
            EndPdfEditing();
            if (closed || result == null) return;
            await OpenFilesAsync([result]);
            Summary.Text = "수정본을 새 탭으로 열었습니다. 원본은 보존했습니다.";
        };
        PdfViewer.Visibility = Visibility.Collapsed; EmptyPreview.Visibility = Visibility.Collapsed;
        InlineEditorHost.Content = pdfEditor; InlineEditorHost.Visibility = Visibility.Visible;
        EditPdfButton.Content = "보기로";
        DocumentTabs.IsEnabled = Files.IsEnabled = MenuToggle.IsEnabled = SmartToggle.IsEnabled = false;
        AllowDrop = false;
        Summary.Text = "본문 클릭 → 직접 입력 → Enter 적용 · 새 PDF로 저장";
        try { await pdfEditor.Ready; } catch (Exception ex) { Summary.Text = ex.Message; }
    }
    void EndPdfEditing()
    {
        pdfEditor?.DisposeEditor(); pdfEditor = null;
        InlineEditorHost.Content = null; InlineEditorHost.Visibility = Visibility.Collapsed;
        EditPdfButton.Content = "수정";
        DocumentTabs.IsEnabled = Files.IsEnabled = MenuToggle.IsEnabled = SmartToggle.IsEnabled = true;
        AllowDrop = true;
    }
}

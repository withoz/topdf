using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
namespace Topdf;
public partial class MainWindow
{
    PdfEditorView? pdfEditor;
    FileJob? editorJob;
    string? editorSource;
    public PdfEditorView? ActivePdfEditor => pdfEditor;
    async void EditPdf(object sender,RoutedEventArgs e)
    {
        if(pdfEditor==null && SelectedJob is {Pdf:not null} job)await StartPdfEditorAsync(job.Pdf,job.Name);
        if(pdfEditor is not {} editor)return;
        try {
            await editor.Ready;
            if(ReferenceEquals(pdfEditor,editor))editor.EnterEditing();
        } catch(OperationCanceledException) {}
        catch(Exception ex) {
            if(ReferenceEquals(pdfEditor,editor))Summary.Text="PDF 편집 준비 실패: "+ex.Message;
        }
    }
    static string? EditorPath(FileJob? job)=>job!=null&&Path.GetExtension(job.Source).Equals(".topdf",StringComparison.OrdinalIgnoreCase)?job.Source:job?.Pdf;
    bool CanChangeDocument(FileJob? next)
    {
        if(pdfEditor==null || ReferenceEquals(next,editorJob) && string.Equals(EditorPath(next),editorSource,StringComparison.OrdinalIgnoreCase))return true;
        if(!pdfEditor.CanClose()){if(editorJob!=null && jobs.Contains(editorJob))Files.SelectedItem=editorJob;return false;}
        EndPdfEditing();return true;
    }
    async Task StartPdfEditorAsync(string source,string name)
    {
        if(pdfEditor!=null){if(string.Equals(editorSource,source,StringComparison.OrdinalIgnoreCase))return;if(!pdfEditor.CanClose())return;EndPdfEditing();}
        var editor=new PdfEditorView(source,name,fitOnOpen:preferSharedPageEditor);pdfEditor=editor;editorSource=source;editorJob=SelectedJob;
        var job=editorJob;
        editor.EditStateChanged+=(_,_)=>{if(job!=null)job.HasUnsavedEdits=editor.HasUnsavedEdits;};
        editor.Saved+=async(_,_)=>{
            if(!ReferenceEquals(pdfEditor,editor))return;var result=editor.ResultPath;EndPdfEditing();
            if(closed||result==null)return;await OpenFilesAsync([result]);Summary.Text="수정본을 새 탭으로 열었습니다. 원본은 보존했습니다.";
        };
        if(preferSharedPageEditor)editor.AttachSharedEditor();
        else {
            editor.NativeZoomCommand=ChangeNativeZoomAsync;editor.NavigateViewingPdf=NavigateUnifiedPreviewAsync;
            editor.ReadViewingPosition=ReadViewingPositionAsync;editor.ToggleNativePagePane=ToggleNativeContentsAsync;
            PreviewHost.Children.Remove(PdfViewer);editor.AttachNativeViewer(PdfViewer,source);
        }
        LayoutRoot.Children.Remove(DocumentTabs);
        DocumentTabs.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty,new System.Windows.Data.Binding("SelectedItem") {Source=Files,Mode=System.Windows.Data.BindingMode.TwoWay});
        editor.AttachDocumentTabs(DocumentTabs);
        WindowActionsHost.Content=null;
        WindowActionsHost.Visibility=Visibility.Collapsed;
        editor.AttachWindowActions(WindowActions);
        InlineEditorHost.Content=editor;InlineEditorHost.Visibility=Visibility.Visible;EmptyPreview.Visibility=Visibility.Collapsed;
        try {
            await editor.Ready;
            if(closed || !ReferenceEquals(pdfEditor,editor))return;
            if(Path.GetExtension(source).Equals(".topdf",StringComparison.OrdinalIgnoreCase)) {
                if(preferSharedPageEditor) {
                    var pdf=await editor.GetViewingPdfAsync();
                    if(ReferenceEquals(pdfEditor,editor)&&editorJob!=null)editorJob.Pdf=pdf;
                } else {
                    await editor.ReturnToViewingAsync(true);
                    if(editorJob!=null&&PdfViewer.Source!=null)editorJob.Pdf=PdfViewer.Source.LocalPath;
                }
            }
            if(preferSharedPageEditor)await editor.SetSharedPageViewAsync(startEditing:true);
            else await editor.SyncViewingZoomAsync();
            Summary.Text="선·글자 도구로 편집 · Esc로 선택";
        } catch(OperationCanceledException) {} catch(Exception ex){if(ReferenceEquals(pdfEditor,editor))Summary.Text="PDF 편집 준비 실패: "+ex.Message;}
    }
    async Task NavigateUnifiedPreviewAsync(string pdf,int page,double zoom)
    {
        viewerInitialization??=InitializeViewerAsync();await viewerInitialization;if(closed)return;
        // A fresh query forces PDF open parameters to apply even when only page/zoom changed.
        var uri=new Uri(Path.GetFullPath(pdf)).AbsoluteUri+"?topdfView="+Guid.NewGuid().ToString("N");allowedPreviewUri=uri;
        var target=uri+"#page="+page+"&zoom="+zoom.ToString("0.#",System.Globalization.CultureInfo.InvariantCulture);
        var loaded=new TaskCompletionSource<bool>();var core=PdfViewer.CoreWebView2;
        void Completed(object? sender,CoreWebView2NavigationCompletedEventArgs e){if(e.NavigationId==previewNavigation)loaded.TrySetResult(e.IsSuccess);}
        core.NavigationCompleted+=Completed;
        try{core.Navigate(target);if(!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(45),lifetime.Token))throw new IOException("수정본 미리보기를 열 수 없습니다.");}
        finally{core.NavigationCompleted-=Completed;}
    }
    void EndPdfEditing()
    {
        if(pdfEditor!=null){
            SmartToggle.IsChecked=false;
            pdfEditor.DetachWindowActions();
            WindowActionsHost.Content=WindowActions;
            WindowActionsHost.Visibility=Visibility.Visible;
            pdfEditor.DetachDocumentTabs();pdfEditor.DetachNativeViewer();pdfEditor.DisposeEditor();
        }
        pdfEditor=null;editorJob=null;editorSource=null;
        InlineEditorHost.Content=null;InlineEditorHost.Visibility=Visibility.Collapsed;
        if(!LayoutRoot.Children.Contains(DocumentTabs))LayoutRoot.Children.Add(DocumentTabs);
        if(!PreviewHost.Children.Contains(PdfViewer))PreviewHost.Children.Insert(0,PdfViewer);
    }
}

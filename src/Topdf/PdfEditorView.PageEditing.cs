using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Topdf;

public partial class PdfEditorView
{
    Point pageDragStart;
    List<PdfEditPage>? draggedPages;
    const string PageDragFormat="TOPDF-PdfEditPages";

    List<PdfEditPage> SelectedPages()=>EditPages.SelectedItems.Cast<PdfEditPage>().OrderBy(plan.Pages.IndexOf).ToList();

    void UsePageSource(string source,PdfEditInfo inspected)
    {
        snapshot=source;
        info=inspected;
        sourceTexts.Clear();searchableText.Clear();
        renders.Clear();
        pagePreviews.Clear();
        viewingPath=viewingPlan=null;
        ClearPageSearch();
    }

    void CommitPageArrangement(History before,int selection)
    {
        selectedIds.Clear();selectedId="";selectedSource=null;
        BindPages(selection);
        Commit(before);
        _=RenderPageAsync();
    }

    void DeleteSelectedPages(object sender,RoutedEventArgs e)
    {
        if(IsBusy||info==null)return;
        ApplyInlineText();
        var pages=SelectedPages();
        if(pages.Count==0)return;
        if(pages.Count==plan.Pages.Count){ShowPageOperationError("최소 한 페이지는 남겨야 합니다.");return;}
        var before=Capture();
        var index=plan.Pages.IndexOf(pages[0]);
        foreach(var page in pages)plan.Pages.Remove(page);
        CommitPageArrangement(before,index);
    }

    void PageActionsOpening(object sender,ContextMenuEventArgs e)
    {
        if(info==null||IsBusy){e.Handled=true;return;}
        var count=EditPages.SelectedItems.Count;
        var index=EditPages.SelectedIndex;
        foreach(var item in EditPages.ContextMenu.Items.OfType<MenuItem>()) {
            item.IsEnabled=(item.Tag as string) switch {
                "delete"=>count>0&&count<plan.Pages.Count,
                "extract"=>count>0,
                "up"=>index>0,
                "down"=>index>=0&&index<plan.Pages.Count-1,
                "ocr"=>index>=0&&!SourceTextItems.Any(),
                _=>index>=0
            };
        }
    }

    void PageContextTarget(object sender,MouseButtonEventArgs e)
    {
        var row=ItemsControl.ContainerFromElement(EditPages,e.OriginalSource as DependencyObject) as ListBoxItem;
        if(row!=null&&!row.IsSelected)EditPages.SelectedItem=row.DataContext;
    }

    void PageDragDown(object sender,MouseButtonEventArgs e)
    {
        pageDragStart=e.GetPosition(EditPages);
        var row=ItemsControl.ContainerFromElement(EditPages,e.OriginalSource as DependencyObject) as ListBoxItem;
        draggedPages=row?.DataContext is PdfEditPage page ? (row.IsSelected?SelectedPages():[page]) : null;
    }

    void PageDragMove(object sender,MouseEventArgs e)
    {
        if(draggedPages is not {Count:>0}||e.LeftButton!=MouseButtonState.Pressed||IsBusy)return;
        var delta=e.GetPosition(EditPages)-pageDragStart;
        if(Math.Abs(delta.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(delta.Y)<SystemParameters.MinimumVerticalDragDistance)return;
        var selected=draggedPages;
        draggedPages=null;
        DragDrop.DoDragDrop(EditPages,new DataObject(PageDragFormat,selected),DragDropEffects.Move);
    }

    void PageDragDrop(object sender,DragEventArgs e)
    {
        if(IsBusy||info==null||e.Data.GetData(PageDragFormat) is not List<PdfEditPage> pages||
            pages.Count==0||pages.Any(page=>!plan.Pages.Contains(page)))return;
        var row=ItemsControl.ContainerFromElement(EditPages,e.OriginalSource as DependencyObject) as ListBoxItem;
        var target=row?.DataContext as PdfEditPage;
        if(target!=null&&pages.Contains(target))return;
        ApplyInlineText();
        var before=Capture();
        foreach(var page in pages)plan.Pages.Remove(page);
        var index=target==null?plan.Pages.Count:plan.Pages.IndexOf(target)+(e.GetPosition(row!).Y>=row!.ActualHeight/2?1:0);
        plan.Pages.InsertRange(index,pages);
        CommitPageArrangement(before,index);
        e.Handled=true;
    }

    async void InsertPdfPages(object sender,RoutedEventArgs e)=>await PickPdfPagesAsync(afterCurrent:true);
    async void MergePdfPages(object sender,RoutedEventArgs e)=>await PickPdfPagesAsync(afterCurrent:false);

    async Task PickPdfPagesAsync(bool afterCurrent)
    {
        if(IsBusy||info==null)return;
        var dialog=new OpenFileDialog{Title=afterCurrent?"현재 쪽 뒤에 PDF 삽입":"끝에 PDF 병합",Filter="PDF|*.pdf"};
        if(dialog.ShowDialog(DialogOwner)!=true)return;
        try {await ImportPdfPagesAsync(dialog.FileName,afterCurrent?EditPages.SelectedIndex+1:plan.Pages.Count);}
        catch(OperationCanceledException) {}
        catch(Exception ex){ShowPageOperationError(ex.Message);}
    }

    public async Task ImportPdfPagesAsync(string file,int index)
    {
        if(IsBusy||info==null)return;
        EndGraphic();ApplyInlineText();
        var before=Capture();
        var originalPages=info.Pages.Length;
        saving=true;EnableEditing(false);
        try {
            var merged=await PdfEditService.AppendPagesAsync(snapshot,file,work,lifetime.Token);
            var inspected=await PdfEditService.InspectAsync(merged,work,lifetime.Token);
            UsePageSource(merged,inspected);
            plan.SourceSha256=inspected.SourceSha256;
            index=Math.Clamp(index,0,plan.Pages.Count);
            plan.Pages.InsertRange(index,inspected.Pages.Skip(originalPages).Select(page=>new PdfEditPage{SourceIndex=page.SourceIndex}));
        } finally {saving=false;EnableEditing(true);}
        CommitPageArrangement(before,index);
    }

    async void ExtractSelectedPages(object sender,RoutedEventArgs e)
    {
        if(IsBusy||info==null)return;
        ApplyInlineText();
        var pages=SelectedPages();
        if(pages.Count==0)return;
        var dialog=new SaveFileDialog{Title="선택 쪽 추출",Filter="PDF|*.pdf",FileName=Path.GetFileNameWithoutExtension(suggestedName)+"-추출.pdf"};
        if(dialog.ShowDialog(DialogOwner)!=true)return;
        var copy=PdfEditService.Clone(plan);
        copy.Pages=pages.Select(page=>copy.Pages[plan.Pages.IndexOf(page)]).ToList();
        saving=true;EnableEditing(false);
        try {
            var pdf=await PdfEditService.ExportAsync(snapshot,work,copy,lifetime.Token);
            await PdfEditService.SaveNewAsync(pdf,dialog.FileName,lifetime.Token);
        } catch(OperationCanceledException) {}
        catch(Exception ex){ShowPageOperationError(ex.Message);}
        finally {saving=false;EnableEditing(true);}
    }

    async void RunPageOcr(object sender,RoutedEventArgs e)
    {
        if(!BuildFeatures.SmartEnabled)return;
        if(IsBusy||info==null||Page==null)return;
        if(SourceTextItems.Any()){ShowPageOperationError("현재 쪽에 이미 선택할 수 있는 텍스트가 있습니다.");return;}
        ApplyInlineText();
        var page=Page;
        var before=Capture();
        var originalPageCount=info.Pages.Length;
        var specification=new PdfEditPlan{SourceSha256=info.SourceSha256,Pages=[new(){SourceIndex=page.SourceIndex}]};
        saving=true;EnableEditing(false);
        try {
            if((await PdfEditService.TextAsync(snapshot,work,page.SourceIndex,lifetime.Token,includeInvisible:true)).Length>0) {
                ShowPageOperationError("현재 쪽에 이미 검색할 수 있는 텍스트가 있습니다.");
                return;
            }
            var singlePage=await PdfEditService.ExportAsync(snapshot,work,specification,lifetime.Token);
            var recognized=await DocumentIntelligence.OcrAsync(singlePage,lifetime.Token);
            var merged=await PdfEditService.AppendPagesAsync(snapshot,recognized,work,lifetime.Token);
            var inspected=await PdfEditService.InspectAsync(merged,work,lifetime.Token);
            UsePageSource(merged,inspected);
            plan.SourceSha256=inspected.SourceSha256;
            page.SourceIndex=originalPageCount;
        } catch(OperationCanceledException){return;}
        catch(Exception ex){ShowPageOperationError(ex.Message);return;}
        finally {saving=false;EnableEditing(true);}
        CommitPageArrangement(before,before.Page);
    }

    void ShowPageOperationError(string message)
    {
        EditStatus.Visibility=Visibility.Visible;
        EditStatus.Text=message;
    }
}

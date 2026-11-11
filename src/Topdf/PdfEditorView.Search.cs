using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Topdf;

public partial class PdfEditorView
{
    sealed record SearchHit(int SourceIndex,PdfSourceText? Original,string? ElementId);
    readonly List<SearchHit> pageSearchHits=[];
    readonly Dictionary<int,PdfSourceText[]> searchableText=[];
    CancellationTokenSource? searchCancellation;
    string searchTerm="";
    long searchedRevision=-1;
    int searchIndex=-1;
    Border? searchMark;

    void OpenPageSearch()
    {
        if(info==null||IsBusy)return;
        PageSearchBar.Visibility=Visibility.Visible;
        PageSearchQuery.Focus();
        PageSearchQuery.SelectAll();
    }
    void ClosePageSearch(object sender,RoutedEventArgs e)
    {
        ClearPageSearch();
        PageSearchBar.Visibility=Visibility.Collapsed;
        EditScroll.Focus();
    }
    void ClearPageSearch()
    {
        searchCancellation?.Cancel();
        searchCancellation=null;
        pageSearchHits.Clear();
        searchIndex=-1;
        searchTerm="";
        if(searchMark!=null)EditCanvas.Children.Remove(searchMark);
        searchMark=null;
    }
    async void PageSearchKey(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Enter)return;
        e.Handled=true;
        await MovePageSearchAsync(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?-1:1);
    }
    async void NextPageSearch(object sender,RoutedEventArgs e)=>await MovePageSearchAsync(1);
    async void PreviousPageSearch(object sender,RoutedEventArgs e)=>await MovePageSearchAsync(-1);

    async Task MovePageSearchAsync(int direction)
    {
        if(info==null||IsBusy)return;
        var query=PageSearchQuery.Text.Trim();
        if(query.Length==0){ClearPageSearch();PageSearchNotice.Text="검색어를 입력하세요.";return;}
        if(query!=searchTerm||searchedRevision!=editRevision) {
            ClearPageSearch();
            using var request=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            searchCancellation=request;
            var source=snapshot;
            var revision=editRevision;
            var pages=PdfEditService.Clone(plan).Pages;
            var hits=new List<SearchHit>();
            PageSearchNotice.Text="찾는 중…";
            try {
                foreach(var page in pages) {
                    request.Token.ThrowIfCancellationRequested();
                    if(!searchableText.TryGetValue(page.SourceIndex,out var text)) {
                        text=await PdfEditService.TextAsync(source,work,page.SourceIndex,request.Token,includeInvisible:true);
                        request.Token.ThrowIfCancellationRequested();
                        searchableText[page.SourceIndex]=text;
                    }
                    bool Matches(string value)=>value.Contains(query,StringComparison.CurrentCultureIgnoreCase);
                    var replaced=page.Elements.Where(item=>item.SourceTextId!=null).Select(item=>item.SourceTextId).ToHashSet();
                    hits.AddRange(text.Where(line=>!replaced.Contains(line.Id)&&Matches(line.Text)).Select(line=>new SearchHit(page.SourceIndex,line,null)));
                    hits.AddRange(page.Elements.Where(item=>Matches(item.Text)).Select(item=>new SearchHit(page.SourceIndex,null,item.Id)));
                }
                if(editRevision!=revision){PageSearchNotice.Text="편집 내용이 변경됐습니다. 다시 찾으세요.";return;}
                pageSearchHits.AddRange(hits);
                searchTerm=query;
                searchedRevision=revision;
            } catch(OperationCanceledException){return;}
            catch(Exception ex){PageSearchNotice.Text=ex.Message;return;}
            finally {if(ReferenceEquals(searchCancellation,request))searchCancellation=null;}
        }
        if(pageSearchHits.Count==0){PageSearchNotice.Text="검색 결과 없음";return;}
        searchIndex=searchIndex<0?(direction>0?0:pageSearchHits.Count-1):(searchIndex+direction+pageSearchHits.Count)%pageSearchHits.Count;
        var hit=pageSearchHits[searchIndex];
        var index=plan.Pages.FindIndex(page=>page.SourceIndex==hit.SourceIndex);
        if(index<0)return;
        if(IsSpreadViewing||IsNativeViewing)await SetSharedPageViewAsync();
        await NavigateToPageAsync(index+1);
        DrawSearchMark();
        if(searchMark!=null)searchMark.BringIntoView();
        PageSearchNotice.Text=$"{searchIndex+1}/{pageSearchHits.Count}";
    }

    void DrawSearchMark()
    {
        if(searchMark!=null)EditCanvas.Children.Remove(searchMark);
        searchMark=null;
        if(Page==null||searchIndex<0||searchIndex>=pageSearchHits.Count)return;
        var hit=pageSearchHits[searchIndex];
        if(Page.SourceIndex!=hit.SourceIndex)return;
        var item=hit.ElementId==null?null:Page.Elements.FirstOrDefault(element=>element.Id==hit.ElementId);
        var text=hit.Original==null?null:DisplayText(hit.Original);
        if(item==null&&text==null)return;
        searchMark=new Border {
            Width=item?.Width??text!.Width,Height=item?.Height??text!.Height,
            Background=new SolidColorBrush(Color.FromArgb(75,255,221,30)),BorderBrush=Brushes.Goldenrod,
            BorderThickness=new Thickness(1),IsHitTestVisible=false,
            RenderTransformOrigin=new(.5,.5),RenderTransform=new RotateTransform(item?.Rotation??text!.Rotation)
        };
        Canvas.SetLeft(searchMark,item?.X??text!.X);
        Canvas.SetTop(searchMark,item?.Y??text!.Y);
        EditCanvas.Children.Add(searchMark);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Topdf;

public partial class PdfEditorView
{
    string? CurrentGraphicKind => Tool switch {
        "선"=>"line", "화살표"=>"arrow", "사각형"=>"rectangle", "타원"=>"ellipse",
        "형광펜"=>"highlight", "밑줄"=>"underline", "취소선"=>"strikeout", "자유곡선"=>"freehand",
        _=>null
    };

    readonly HashSet<string> bakedSpreadGraphics=[];
    sealed record SpreadStroke(Grid PageHost, Canvas Overlay, PageTile Tile, PdfEditElement Element, List<Point> Points);
    SpreadStroke? spreadStroke;
    SpreadStroke? spreadLine;
    string? spreadLineTool;
    bool CanEditSpread => IsSpreadViewing&&ViewColumns>1;
    Canvas? spreadStampOverlay;
    Image? spreadStampPreview;
    Canvas? spreadSelectionOverlay;

    void RefreshSpreadSelection()
    {
        var overlay=spreadSelectionOverlay;
        spreadSelectionOverlay=null;
        if(overlay!=null)RefreshSpreadGraphics(overlay,new RoutedEventArgs());
    }

    void HideSpreadStampPreview()
    {
        if(spreadStampPreview!=null)spreadStampOverlay?.Children.Remove(spreadStampPreview);
        spreadStampOverlay=null;
        spreadStampPreview=null;
    }

    void SpreadPointerLeft(object sender,MouseEventArgs e) => HideSpreadStampPreview();

    void ShowSpreadStampPreview(Grid host,Canvas overlay,MouseEventArgs e)
    {
        if(stamp==null||IsBusy||host.ActualWidth<=0||host.ActualHeight<=0)return;
        if(!ReferenceEquals(spreadStampOverlay,overlay)){
            HideSpreadStampPreview();
            RefreshSpreadGraphics(overlay,new RoutedEventArgs());
        }
        var source=LoadImage(stamp);
        var bounds=StampBoundsForPage(source,SpreadPoint(host,overlay,e),overlay.Width,overlay.Height);
        if(bounds.IsEmpty)return;
        spreadStampOverlay=overlay;
        spreadStampPreview??=new Image { Source=source,Stretch=Stretch.Fill,Opacity=.65,IsHitTestVisible=false };
        spreadStampPreview.Width=bounds.Width;
        spreadStampPreview.Height=bounds.Height;
        Canvas.SetLeft(spreadStampPreview,bounds.X);
        Canvas.SetTop(spreadStampPreview,bounds.Y);
        if(!overlay.Children.Contains(spreadStampPreview))overlay.Children.Add(spreadStampPreview);
    }

    void PlaceSpreadStamp(Grid host,Canvas overlay,PageTile tile,Point point)
    {
        if(stamp==null)return;
        var bounds=StampBoundsForPage(LoadImage(stamp),point,overlay.Width,overlay.Height);
        if(bounds.IsEmpty)return;
        var item=new PdfEditElement {
            Kind="stamp",ImagePath=stamp,X=bounds.X,Y=bounds.Y,Width=bounds.Width,Height=bounds.Height
        };
        HideSpreadStampPreview();
        CommitSpreadElement(host,overlay,tile,item);
        EditTool.SelectedIndex=1;
    }

    void RefreshSpreadGraphics(object sender, RoutedEventArgs e)
    {
        if(sender is not Canvas overlay)return;
        overlay.Children.Clear();
        if(overlay.DataContext is not PageTile tile||info==null||tile.Index>=plan.Pages.Count)return;
        var page=plan.Pages[tile.Index];
        var dimensions=info.Pages[page.SourceIndex];
        var rotated=page.Rotation is 90 or 270;
        overlay.Width=rotated?dimensions.Height:dimensions.Width;
        overlay.Height=rotated?dimensions.Width:dimensions.Height;
        foreach(var element in page.Elements.Where(x=>!bakedSpreadGraphics.Contains(x.Id))){
            if(Graphics.Contains(element.Kind))AddSpreadGraphic(overlay,element);
            else if(element.Kind=="stamp"){
                var image=new Image {Source=LoadImage(element.ImagePath!),Stretch=Stretch.Fill,Opacity=element.ImageOpacity};
                PlaceSpreadVisual(overlay,image,element);
            }else if(element.Kind is "text" or "note" or "replace"){
                if(element.Kind=="replace"&&string.IsNullOrWhiteSpace(element.Text))continue;
                var note=element.Kind=="note";
                var text=new TextBlock {
                    Text=element.Text,FontFamily=new FontFamily(element.FontFamily),
                    TextAlignment=AlignmentOf(element),FontSize=element.FontSize,
                    LineHeight=element.FontSize*1.35,TextWrapping=TextWrapping.Wrap,
                    Foreground=(Brush)new BrushConverter().ConvertFromString(element.Color)!
                };
                var border=new AnnotationBorder {
                    Child=text,Padding=new Thickness(note?7:1),ClipToBounds=true,
                    Background=note?new SolidColorBrush(Color.FromRgb(255,243,175)):TextBoxFillBrush(element)
                };
                PlaceSpreadVisual(overlay,border,element);
            }
        }
        if(Tool=="선택"&&tile.Index==EditPages.SelectedIndex&&selectedIds.Count>0){
            spreadSelectionOverlay=overlay;
            var items=page.Elements.Where(item=>selectedIds.Contains(item.Id)).ToList();
            foreach(var item in items){
                var outline=new Border {
                    Width=item.Width,Height=item.Height,BorderBrush=Brushes.DodgerBlue,
                    BorderThickness=new Thickness(Math.Max(1,overlay.Width/tile.Width)),
                    RenderTransformOrigin=new Point(.5,.5),RenderTransform=new RotateTransform(item.Rotation),
                    Tag=item,IsHitTestVisible=false
                };
                Canvas.SetLeft(outline,item.X);
                Canvas.SetTop(outline,item.Y);
                overlay.Children.Add(outline);
            }
            if(items.Count>0&&items.All(item=>item.Kind!="replace"))
                AddSpreadRotationHandle(overlay,tile,items);
            if(items.Count>0)AddSpreadResizeHandle(overlay,items);
        }
    }

    bool SelectSpreadObject(Grid host,Canvas overlay,PageTile tile,Point point,ModifierKeys modifiers)
    {
        var oldOverlay=spreadSelectionOverlay;
        var samePage=tile.Index==EditPages.SelectedIndex;
        var hits=plan.Pages[tile.Index].Elements.AsEnumerable().Reverse()
            .Where(item=>HitAnnotation(item,point,host.ActualWidth/overlay.Width)).ToArray();
        var hit=hits.FirstOrDefault();
        if(hits.Length>1&&samePage&&modifiers.HasFlag(ModifierKeys.Alt))
            hit=hits[(Array.FindIndex(hits,item=>item.Id==selectedId)+1)%hits.Length];
        if(!samePage||hit==null||((modifiers&(ModifierKeys.Control|ModifierKeys.Shift))==0&&
            (!selectedIds.Contains(hit.Id)||modifiers.HasFlag(ModifierKeys.Alt))))selectedIds.Clear();
        if(hit!=null&&(!modifiers.HasFlag(ModifierKeys.Control)||!selectedIds.Remove(hit.Id)))selectedIds.Add(hit.Id);
        selectedId=hit!=null&&selectedIds.Contains(hit.Id)?hit.Id:selectedIds.FirstOrDefault()??"";
        selectedSource=null;
        updating=true;
        try{EditPages.SelectedIndex=tile.Index;}
        finally{updating=false;}
        EditPageNumber.Text=(tile.Index+1).ToString();
        SpreadPages.Focus();
        SyncRibbonObjectProperties(Selected);
        RefreshObjectProperties();
        if(oldOverlay!=null&&!ReferenceEquals(oldOverlay,overlay))RefreshSpreadGraphics(oldOverlay,new RoutedEventArgs());
        RefreshSpreadGraphics(overlay,new RoutedEventArgs());
        return hit!=null&&selectedIds.Contains(hit.Id);
    }

    void SpreadOverlayContextChanged(object sender,DependencyPropertyChangedEventArgs e) =>
        RefreshSpreadGraphics(sender,new RoutedEventArgs());

    static void AddSpreadGraphic(Canvas overlay,PdfEditElement element) =>
        PlaceSpreadVisual(overlay,CreateGraphicVisual(element),element);

    static void PlaceSpreadVisual(Canvas overlay,FrameworkElement visual,PdfEditElement element,int? index=null)
    {
        visual.Width=element.Width;
        visual.Height=element.Height;
        visual.IsHitTestVisible=false;
        visual.Tag=element;
        visual.RenderTransformOrigin=new Point(.5,.5);
        visual.RenderTransform=new RotateTransform(element.Rotation);
        Canvas.SetLeft(visual,element.X);
        Canvas.SetTop(visual,element.Y);
        if(index is int position)overlay.Children[position]=visual;
        else overlay.Children.Add(visual);
    }

    static Point SpreadPoint(SpreadStroke stroke,MouseEventArgs e) => SpreadPoint(stroke.PageHost,stroke.Overlay,e);

    static Point SpreadPoint(Grid host,Canvas overlay,MouseEventArgs e)
    {
        var point=e.GetPosition(host);
        return new(Math.Clamp(point.X/host.ActualWidth,0,1)*overlay.Width,
            Math.Clamp(point.Y/host.ActualHeight,0,1)*overlay.Height);
    }

    void StartSpreadStroke(object sender,MouseButtonEventArgs e)
    {
        if(!CanEditSpread||IsBusy||
            sender is not Grid {DataContext:PageTile tile} host||
            host.Children.OfType<Viewbox>().FirstOrDefault()?.Child is not Canvas overlay||
            host.ActualWidth<=0||host.ActualHeight<=0)return;
        for(var node=e.OriginalSource as DependencyObject;node!=null&&node!=host;node=VisualTreeHelper.GetParent(node))
            if(node is Thumb)return;
        if(Tool=="선택"){
            RefreshSpreadGraphics(overlay,new RoutedEventArgs());
            if(overlay.Width>0&&overlay.Height>0){
                var point=SpreadPoint(host,overlay,e);
                var modifiers=Keyboard.Modifiers;
                if(SelectSpreadObject(host,overlay,tile,point,modifiers)&&modifiers==ModifierKeys.None)
                    BeginSpreadMove(host,overlay,SpreadPoint(host,overlay,e));
            }
            e.Handled=true;
            return;
        }
        if(Tool=="도장"){
            e.Handled=true;
            if(stamp==null){ChooseStamp(this,new RoutedEventArgs());return;}
            RefreshSpreadGraphics(overlay,new RoutedEventArgs());
            if(overlay.Width>0&&overlay.Height>0)PlaceSpreadStamp(host,overlay,tile,SpreadPoint(host,overlay,e));
            return;
        }
        if(CurrentGraphicKind is not {} kind)return;
        if(spreadLineTool!=kind||!ReferenceEquals(spreadLine?.Tile,tile))
            CancelSpreadStroke();
        RefreshSpreadGraphics(overlay,new RoutedEventArgs());
        if(!(overlay.Width>0&&overlay.Height>0))return;
        if(!host.CaptureMouse())return;
        var element=new PdfEditElement {Kind=spreadLine?.Element.Kind??kind};
        SetObjectStyle(element);
        if(kind=="highlight"&&ColorValue=="#111111")element.Color="#FFEB3B";
        spreadStroke=new(host,overlay,tile,element,[]);
        spreadStroke.Points.Add(spreadLine?.Points[0]??SpreadPoint(spreadStroke,e));
        SpreadPages.Focus();
        e.Handled=true;
    }

    bool UpdateSpreadStroke(SpreadStroke stroke,MouseEventArgs e)
    {
        return UpdateSpreadGraphic(stroke,SpreadPoint(stroke,e),Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
    }

    bool UpdateSpreadGraphic(SpreadStroke stroke,Point point,bool constrain)
    {
        if(stroke.PageHost.ActualWidth<=0||stroke.PageHost.ActualHeight<=0||
            !ReferenceEquals(stroke.PageHost.DataContext,stroke.Tile))return false;
        var start=stroke.Points[0];
        if(stroke.Element.Kind is "underline" or "strikeout")point.Y=start.Y;
        if((stroke.Element.Kind is "line" or "arrow")&&constrain){
            if(Math.Abs(point.X-start.X)>=Math.Abs(point.Y-start.Y))point.Y=start.Y;
            else point.X=start.X;
        }
        if(stroke.Element.Kind=="freehand"){
            if(stroke.Points.Count<10000&&point!=stroke.Points[^1])stroke.Points.Add(point);
        }else if(stroke.Points.Count==1)stroke.Points.Add(point);
        else stroke.Points[1]=point;
        var item=stroke.Element;
        item.X=stroke.Points.Min(p=>p.X);
        item.Y=stroke.Points.Min(p=>p.Y);
        var width=stroke.Points.Max(p=>p.X)-item.X;
        var height=stroke.Points.Max(p=>p.Y)-item.Y;
        var minimum=6*stroke.Overlay.Width/stroke.PageHost.ActualWidth;
        var valid=item.Kind is "rectangle" or "ellipse" or "highlight"
            ? width>=minimum&&height>=minimum
            : width*width+height*height>=minimum*minimum;
        RefreshSpreadGraphics(stroke.Overlay,new RoutedEventArgs());
        if(!valid)return false;
        var pad=item.Kind=="arrow"?item.StrokeWidth*4:0;
        var right=Math.Min(stroke.Overlay.Width,item.X+Math.Max(1,width)+pad);
        var bottom=Math.Min(stroke.Overlay.Height,item.Y+Math.Max(1,height)+pad);
        item.X=Math.Max(0,item.X-pad);
        item.Y=Math.Max(0,item.Y-pad);
        item.Width=Math.Max(1,right-item.X);
        item.Height=Math.Max(1,bottom-item.Y);
        var points=item.Kind=="arrow"?stroke.Points.AsEnumerable().Reverse():stroke.Points;
        item.Points=points.Select(p=>new[]{(p.X-item.X)/item.Width,(p.Y-item.Y)/item.Height}).ToList();
        AddSpreadGraphic(stroke.Overlay,item);
        return true;
    }

    void MoveSpreadStroke(object sender,MouseEventArgs e)
    {
        if(spreadMove is {} move&&ReferenceEquals(move.Host,sender)){
            if(e.LeftButton==MouseButtonState.Pressed)MoveSpreadObjects(move,SpreadPoint(move.Host,move.Overlay,e));
            e.Handled=true;
            return;
        }
        if(CanEditSpread&&Tool=="도장"&&sender is Grid host&&
            host.Children.OfType<Viewbox>().FirstOrDefault()?.Child is Canvas overlay){
            ShowSpreadStampPreview(host,overlay,e);
            return;
        }
        var stroke=spreadStroke??spreadLine;
        if(stroke==null||!ReferenceEquals(stroke.PageHost,sender))return;
        UpdateSpreadStroke(stroke,e);
        e.Handled=true;
    }

    void FinishSpreadStroke(object sender,MouseButtonEventArgs e)
    {
        if(ReferenceEquals(spreadMove?.Host,sender)){
            FinishSpreadMove(false);
            e.Handled=true;
            return;
        }
        if(spreadStroke is not {} stroke||!ReferenceEquals(stroke.PageHost,sender))return;
        CompleteSpreadStroke(stroke,SpreadPoint(stroke,e),Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled=true;
    }

    void CompleteSpreadStroke(SpreadStroke stroke,Point end,bool constrain)
    {
        var valid=UpdateSpreadGraphic(stroke,end,constrain);
        var continuing=spreadLine!=null;
        var tool=CurrentGraphicKind;
        CancelSpreadStroke();
        if(valid)CommitSpreadGraphic(stroke);
        if(tool is "line" or "arrow" && (continuing||!valid)){
            var start=valid?stroke.Points[^1]:stroke.Points[0];
            var next=new PdfEditElement {Kind=valid?"line":stroke.Element.Kind};
            SetObjectStyle(next);
            spreadLine=stroke with {Element=next,Points=[start]};
            spreadLineTool=tool;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    void CommitSpreadGraphic(SpreadStroke stroke) =>
        CommitSpreadElement(stroke.PageHost,stroke.Overlay,stroke.Tile,stroke.Element);

    void CommitSpreadElement(Grid host,Canvas overlay,PageTile tile,PdfEditElement element)
    {
        if(ReferenceEquals(host.DataContext,tile)&&tile.Index<plan.Pages.Count){
            var before=Capture();
            updating=true;
            try {EditPages.SelectedIndex=tile.Index;}
            finally {updating=false;}
            selectedId="";
            selectedIds.Clear();
            RefreshSpreadSelection();
            EditPageNumber.Text=(tile.Index+1).ToString();
            plan.Pages[tile.Index].Elements.Add(element);
            Commit(before,keepSpread:true);
            RefreshSpreadGraphics(overlay,new RoutedEventArgs());
        }
    }

    void SpreadCaptureLost(object sender,MouseEventArgs e)
    {
        if(ReferenceEquals(spreadMove?.Host,sender))FinishSpreadMove(true);
        if(ReferenceEquals(spreadStroke?.PageHost,sender))CancelSpreadStroke();
    }

    void CancelSpreadStroke()
    {
        var stroke=spreadStroke??spreadLine;
        spreadStroke=null;
        spreadLine=null;
        spreadLineTool=null;
        if(stroke==null)return;
        if(stroke.PageHost.IsMouseCaptured)stroke.PageHost.ReleaseMouseCapture();
        RefreshSpreadGraphics(stroke.Overlay,new RoutedEventArgs());
    }
    void FinishSpreadLine(object sender,MouseButtonEventArgs e)
    {
        if(spreadLine==null)return;
        CancelSpreadStroke();
        e.Handled=true;
    }
    async Task RestoreSpreadHistoryAsync(Stack<History> from,Stack<History> to)
    {
        if(from.Count==0)return;
        to.Push(Capture());
        Restore(from.Pop(),renderEditor:false);
        SetDirty(!IsClean());
        await ReturnToViewingAsync(true);
        CommandManager.InvalidateRequerySuggested();
    }
}

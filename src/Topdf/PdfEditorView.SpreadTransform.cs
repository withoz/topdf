using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Topdf;

public partial class PdfEditorView
{
    sealed record SpreadMove(Grid Host,Canvas Overlay,Point Start,History Before,
        List<PdfEditElement> Items,PdfEditElement[] Originals);
    SpreadMove? spreadMove;
    Canvas? spreadMovePreviewPage;
    readonly List<(FrameworkElement Visual, double Opacity)> hiddenSpreadMoveVisuals = [];
    readonly List<(FrameworkElement Source, PdfEditElement Original, FrameworkElement Preview)> spreadMovePreviews = [];

    bool PreviewSpreadMove(SpreadMove move, Point point)
    {
        var target = SpreadDropTarget(move, point);
        if (target is not { } drop || drop.Offset is not Vector offset)
        {
            ClearSpreadMovePreview();
            return false;
        }

        if (spreadMovePreviewPage != drop.Overlay || spreadMovePreviews.Any(pair =>
            !drop.Overlay.Children.Contains(pair.Preview) || !move.Overlay.Children.Contains(pair.Source)))
        {
            ClearSpreadMovePreview();
            spreadMovePreviewPage = drop.Overlay;
            for (var i = 0; i < move.Items.Count; i++)
            {
                var item = move.Items[i];
                var source = move.Overlay.Children.OfType<FrameworkElement>()
                    .FirstOrDefault(visual => visual is not System.Windows.Shapes.Path && ReferenceEquals(visual.Tag, item));
                if (source == null) continue;
                if(CreateSpreadObjectVisual(item) is not {} preview)continue;
                PlaceSpreadVisual(drop.Overlay,preview,item);
                preview.Tag=null;
                preview.Opacity*=.86;
                spreadMovePreviews.Add((source, move.Originals[i], preview));
                UpdateNoteRegionVisual(move.Overlay, item, outlineOnly: true);
            }
        }

        foreach(var visual in move.Overlay.Children.OfType<FrameworkElement>())
        {
            var isMovedObject = visual is not System.Windows.Shapes.Path &&
                visual.Tag is PdfEditElement item && move.Items.Contains(item);
            var isHandle = visual is Thumb || Equals(visual.Tag,"spread-rotation-guide");
            if((isMovedObject || isHandle) && !hiddenSpreadMoveVisuals.Any(entry=>entry.Visual==visual))
            {
                hiddenSpreadMoveVisuals.Add((visual,visual.Opacity));
                visual.Opacity=0;
            }
        }

        foreach (var (_, item, preview) in spreadMovePreviews)
        {
            preview.RenderTransform = new RotateTransform(item.Rotation);
            Canvas.SetLeft(preview, item.X + offset.X);
            Canvas.SetTop(preview, item.Y + offset.Y);
        }
        return true;
    }

    void ClearSpreadMovePreview()
    {
        foreach (var (_, _, preview) in spreadMovePreviews)
            spreadMovePreviewPage?.Children.Remove(preview);
        spreadMovePreviews.Clear();
        if (spreadMove is { } move)
            foreach (var item in move.Items) UpdateNoteRegionVisual(move.Overlay, item);
        foreach(var (visual, opacity) in hiddenSpreadMoveVisuals)visual.Opacity=opacity;
        hiddenSpreadMoveVisuals.Clear();
        spreadMovePreviewPage = null;
    }

    PdfEditElement[] SpreadOriginals(History history,List<PdfEditElement> items) =>
        items.Select(item=>history.Plan.Pages[history.Page].Elements.Single(old=>old.Id==item.Id)).ToArray();

    void BeginSpreadMove(Grid host,Canvas overlay,Point point)
    {
        var items=Objects();
        if(items.Count==0||!host.CaptureMouse())return;
        var before=Capture();
        spreadMove=new(host,overlay,point,before,items,SpreadOriginals(before,items));
        cancelEditGesture=()=>FinishSpreadMove(true);
        CommandManager.InvalidateRequerySuggested();
    }

    void MoveSpreadObjects(SpreadMove move,Point point)
    {
        var delta=point-move.Start;
        var scale=move.Host.ActualWidth/move.Overlay.Width;
        if(Math.Abs(delta.X)*scale<SystemParameters.MinimumHorizontalDragDistance&&
            Math.Abs(delta.Y)*scale<SystemParameters.MinimumVerticalDragDistance&&
            !SpreadTransformChanged(move.Items,move.Originals))return;
        var bounds=SelectionBounds(move.Originals);
        var x=Math.Clamp(delta.X,-bounds.Left,Math.Max(-bounds.Left,move.Overlay.Width-bounds.Right));
        var y=Math.Clamp(delta.Y,-bounds.Top,Math.Max(-bounds.Top,move.Overlay.Height-bounds.Bottom));
        for(var i=0;i<move.Items.Count;i++){
            move.Items[i].X=move.Originals[i].X+x;
            move.Items[i].Y=move.Originals[i].Y+y;
        }
        UpdateSpreadTransformPreview(move.Overlay);
    }

    void FinishSpreadMove(bool canceled, Point? dropPoint = null)
    {
        var move=spreadMove;
        if(move==null)return;
        spreadMove=null;
        ClearSpreadMovePreview();
        cancelEditGesture=null;
        if(move.Host.IsMouseCaptured)move.Host.ReleaseMouseCapture();
        if(!canceled && dropPoint is Point point && TryDropOnAnotherSpreadPage(move,point))return;
        _=CompleteSpreadTransformAsync(move.Before,move.Items,move.Originals,canceled);
    }

    static Grid? FindSpreadDropSurface(UIElement parent, Point point)
    {
        if (parent is Grid { Name: "SpreadPageSurface", DataContext: PageTile } host)
            return new Rect(host.RenderSize).Contains(point) ? host : null;
        for (var index = VisualTreeHelper.GetChildrenCount(parent)-1; index >= 0; index--)
        {
            if (VisualTreeHelper.GetChild(parent,index) is not UIElement { IsVisible: true } child) continue;
            var childPoint = parent.TranslatePoint(point, child);
            if (FindSpreadDropSurface(child, childPoint) is {} surface) return surface;
        }
        return null;
    }

    (PageTile Tile, Canvas Overlay, Vector? Offset)? SpreadDropTarget(SpreadMove move, Point point)
    {
        if(!CanEditSpread || IsBusy || move.Items.Any(item=>item.Kind=="replace"))return null;
        var host=FindSpreadDropSurface(SpreadPages,point);
        if(host?.DataContext is not PageTile tile || tile.Index==move.Before.Page ||
            tile.Index<0 || tile.Index>=plan.Pages.Count ||
            host.Children.OfType<Viewbox>().FirstOrDefault()?.Child is not Canvas overlay ||
            host.ActualWidth<=0 || host.ActualHeight<=0 || overlay.Width<=0 || overlay.Height<=0)return null;
        var local=SpreadPages.TranslatePoint(point,host);
        var pointer=new Point(local.X/host.ActualWidth*overlay.Width,local.Y/host.ActualHeight*overlay.Height);
        var bounds=SelectionBounds(move.Originals);
        if(bounds.Width>overlay.Width || bounds.Height>overlay.Height)
            return (tile, overlay, null);
        var delta=pointer-move.Start;
        return (tile, overlay, new Vector(
            Math.Clamp(delta.X,-bounds.Left,overlay.Width-bounds.Right),
            Math.Clamp(delta.Y,-bounds.Top,overlay.Height-bounds.Bottom)));
    }

    bool TryDropOnAnotherSpreadPage(SpreadMove move, Point point)
    {
        if(SpreadDropTarget(move,point) is not { } drop)return false;
        if(drop.Offset is not Vector offset){
            _=CompleteSpreadTransformAsync(move.Before,move.Items,move.Originals,true);
            ShowPageOperationError("선택한 객체가 대상 페이지보다 큽니다.");
            return true;
        }
        var (tile, overlay, _) = drop;
        var source=plan.Pages[move.Before.Page];
        var target=plan.Pages[tile.Index];
        for(var i=0;i<move.Items.Count;i++){
            var item=move.Items[i];
            source.Elements.Remove(item);
            item.X=move.Originals[i].X+offset.X;
            item.Y=move.Originals[i].Y+offset.Y;
            target.Elements.Add(item);
        }
        updating=true;
        try{EditPages.SelectedIndex=tile.Index;}
        finally{updating=false;}
        EditPageNumber.Text=(tile.Index+1).ToString();
        Commit(move.Before,keepSpread:true);
        RefreshSpreadGraphics(move.Overlay,new RoutedEventArgs());
        RefreshSpreadGraphics(overlay,new RoutedEventArgs());
        _=RefreshSpreadAfterTransformAsync(move.Items);
        return true;
    }

    void AddSpreadResizeHandle(Canvas overlay,List<PdfEditElement> items)
    {
        var handle=new Thumb {
            Tag="spread-resize",Background=Brushes.DodgerBlue,Cursor=Cursors.SizeNWSE,
            Focusable=false,ToolTip="끌어서 크기 조절"
        };
        System.Windows.Automation.AutomationProperties.SetName(handle,"크기 조절");
        overlay.Children.Add(handle);
        PositionSpreadResizeHandle(handle,overlay,items);
        History? before=null;
        PdfEditElement[] originals=[];
        Point start=default;
        Action cancel=handle.CancelDrag;
        handle.DragStarted+=(_,_)=>{
            if(IsBusy||!CanEditSpread){handle.CancelDrag();return;}
            SpreadPages.Focus();
            before=Capture();
            originals=SpreadOriginals(before,items);
            start=Mouse.GetPosition(overlay);
            cancelEditGesture=cancel;
            CommandManager.InvalidateRequerySuggested();
        };
        handle.DragDelta+=(_,_)=>{
            if(before==null)return;
            ResizeSelectionFrom(items,originals,Mouse.GetPosition(overlay)-start);
            UpdateSpreadTransformPreview(overlay);
        };
        handle.DragCompleted+=async (_,e)=>{
            if(ReferenceEquals(cancelEditGesture,cancel))cancelEditGesture=null;
            var snapshot=before;
            before=null;
            if(snapshot!=null)await CompleteSpreadTransformAsync(snapshot,items,originals,e.Canceled);
        };
    }

    void PositionSpreadResizeHandle(Thumb handle,Canvas overlay,List<PdfEditElement> items)
    {
        var scale=overlay.DataContext is PageTile tile?Math.Max(.01,tile.Width/overlay.Width):1;
        handle.Width=handle.Height=10/scale;
        var corner=ResizeCorner(items,SelectionBounds(items));
        var radius=handle.Width/2;
        corner.X=Math.Clamp(corner.X,radius,Math.Max(radius,overlay.Width-radius));
        corner.Y=Math.Clamp(corner.Y,radius,Math.Max(radius,overlay.Height-radius));
        PlaceResizeHandle(handle,corner);
    }

    void UpdateSpreadTransformPreview(Canvas overlay)
    {
        var items=Objects();
        foreach(var outline in overlay.Children.OfType<FrameworkElement>().ToArray()){
            if(outline.Tag is not PdfEditElement item)continue;
            if(outline is System.Windows.Shapes.Path && item.NoteRegion != null){
                UpdateNoteRegionVisual(overlay,item);
                continue;
            }
            if(outline is Canvas && Graphics.Contains(item.Kind) && (outline.Width!=item.Width || outline.Height!=item.Height)){
                PlaceSpreadVisual(overlay,CreateGraphicVisual(item),item,overlay.Children.IndexOf(outline));
                continue;
            }
            outline.Width=item.Width;
            outline.Height=item.Height;
            outline.RenderTransform=new RotateTransform(item.Rotation);
            Canvas.SetLeft(outline,item.X);
            Canvas.SetTop(outline,item.Y);
        }
        if(items.Count==0)return;
        foreach(var handle in overlay.Children.OfType<Thumb>()){
            if(Equals(handle.Tag,"spread-resize"))PositionSpreadResizeHandle(handle,overlay,items);
            else if(Equals(handle.Tag,"object-rotation")&&overlay.DataContext is PageTile tile&&
                overlay.Children.OfType<System.Windows.Shapes.Line>().FirstOrDefault(line=>Equals(line.Tag,"spread-rotation-guide")) is {} guide)
                PositionRotationHandle(handle,guide,items,Math.Max(.01,tile.Width/overlay.Width),overlay.Width,overlay.Height);
        }
    }

    static bool SpreadTransformChanged(List<PdfEditElement> items,PdfEditElement[] originals) =>
        items.Zip(originals).Any(pair=>pair.First.X!=pair.Second.X||pair.First.Y!=pair.Second.Y||
            pair.First.Width!=pair.Second.Width||pair.First.Height!=pair.Second.Height||
            pair.First.FontSize!=pair.Second.FontSize||pair.First.AutoWidth!=pair.Second.AutoWidth||
            pair.First.ManualNoteHeight!=pair.Second.ManualNoteHeight);

    async Task CompleteSpreadTransformAsync(History before,List<PdfEditElement> items,PdfEditElement[] originals,bool canceled)
    {
        if(canceled||!CanEditSpread){
            for(var i=0;i<items.Count;i++){
                items[i].X=originals[i].X;
                items[i].Y=originals[i].Y;
                items[i].Width=originals[i].Width;
                items[i].Height=originals[i].Height;
                items[i].FontSize=originals[i].FontSize;
                items[i].AutoWidth=originals[i].AutoWidth;
                items[i].ManualNoteHeight=originals[i].ManualNoteHeight;
            }
            RefreshSpreadSelection();
        }else if(SpreadTransformChanged(items,originals)){
            Commit(before,keepSpread:true);
            await RefreshSpreadAfterTransformAsync(items);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    async Task RefreshSpreadAfterTransformAsync(List<PdfEditElement> items)
    {
        if(items.Any(item=>bakedSpreadGraphics.Contains(item.Id)||item.Kind=="replace")){
            await UpdateViewingAsync(true,true);
        }else{
            RefreshCurrentSpreadPage();
            foreach (var source in items.Select(item => item.NoteRegion?.SourceIndex).OfType<int>().Distinct())
            {
                var index = plan.Pages.FindIndex(page => page.SourceIndex == source);
                if (index >= 0 && index != EditPages.SelectedIndex) RefreshSpreadPage(SpreadPages, index);
            }
            RefreshObjectProperties();
        }
    }
}

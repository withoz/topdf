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

    void FinishSpreadMove(bool canceled)
    {
        var move=spreadMove;
        if(move==null)return;
        spreadMove=null;
        cancelEditGesture=null;
        if(move.Host.IsMouseCaptured)move.Host.ReleaseMouseCapture();
        _=CompleteSpreadTransformAsync(move.Before,move.Items,move.Originals,canceled);
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
            if(outline is Canvas && item.Kind=="highlight" && outline.Width!=item.Width){
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
            pair.First.FontSize!=pair.Second.FontSize||pair.First.AutoWidth!=pair.Second.AutoWidth);

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
        if(items.Any(item=>bakedSpreadGraphics.Contains(item.Id)||item.Kind is "text" or "note" or "replace")){
            await UpdateViewingAsync(true,true);
        }else{
            RefreshSpreadSelection();
            RefreshObjectProperties();
        }
    }
}

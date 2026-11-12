using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Topdf;

public partial class PdfEditorView
{
    void AddSpreadRotationHandle(Canvas overlay,PageTile tile,List<PdfEditElement> items)
    {
        var guide=new Line { Tag="spread-rotation-guide",Stroke=Brushes.SlateGray,IsHitTestVisible=false };
        var handle=CreateRotationHandle();
        overlay.Children.Add(guide);
        overlay.Children.Add(handle);

        void Position() => PositionRotationHandle(handle,guide,items,
            Math.Max(.01,tile.Width/overlay.Width),overlay.Width,overlay.Height);
        Position();
        handle.Loaded+=(_,_)=>Position();
        if(VisualTreeHelper.GetParent(overlay) is FrameworkElement container){
            SizeChangedEventHandler resized=(_,_)=>Position();
            handle.Loaded+=(_,_)=>container.SizeChanged+=resized;
            handle.Unloaded+=(_,_)=>container.SizeChanged-=resized;
        }

        History? before=null;
        PdfEditElement[] originals=[];
        Point pivot=default;
        double initialAngle=0;
        Action cancel=handle.CancelDrag;
        handle.DragStarted+=(_,_)=>{
            if(IsBusy||!ReferenceEquals(overlay.DataContext,tile)){handle.CancelDrag();return;}
            SpreadPages.Focus();
            before=Capture();
            var ids=items.Select(item=>item.Id).ToHashSet();
            originals=before.Plan.Pages[tile.Index].Elements.Where(item=>ids.Contains(item.Id)).ToArray();
            var bounds=SelectionBounds(items);
            pivot=new(bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2);
            initialAngle=PointerAngle(Mouse.GetPosition(overlay),pivot);
            cancelEditGesture=cancel;
            CommandManager.InvalidateRequerySuggested();
        };
        handle.DragDelta+=(_,_)=>{
            if(before==null)return;
            var angle=PointerAngle(Mouse.GetPosition(overlay),pivot)-initialAngle;
            if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))angle=Math.Round(angle/15)*15;
            if(!RotateSelectionFrom(items,originals,pivot,(int)Math.Round(angle)))return;
            UpdateSpreadTransformPreview(overlay);
        };
        handle.DragCompleted+=async (_,e)=>{
            if(ReferenceEquals(cancelEditGesture,cancel))cancelEditGesture=null;
            var snapshot=before;
            before=null;
            if(snapshot==null)return;
            if(e.Canceled||!CanEditSpread||!ReferenceEquals(overlay.DataContext,tile)){
                for(var index=0;index<items.Count;index++){
                    items[index].X=originals[index].X;
                    items[index].Y=originals[index].Y;
                    items[index].Rotation=originals[index].Rotation;
                }
                RefreshSpreadSelection();
            }else if(items.Zip(originals).Any(pair=>pair.First.Rotation!=pair.Second.Rotation||
                pair.First.X!=pair.Second.X||pair.First.Y!=pair.Second.Y)){
                Commit(snapshot,keepSpread:true);
                await RefreshSpreadAfterTransformAsync(items);
            }
            CommandManager.InvalidateRequerySuggested();
        };
    }
}

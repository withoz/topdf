using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Topdf;

public partial class PdfEditorView
{
    Thumb? DrawRotationHandle(List<PdfEditElement> items)
    {
        if (IsViewing || EditTool.SelectedIndex != 1 || items.Any(x => x.Kind == "replace")) return null;
        var existing=EditCanvas.Children.OfType<Thumb>().FirstOrDefault(x=>Equals(x.Tag,"object-rotation"));
        if(existing!=null){PlaceRotationHandle(existing,items);return existing;}

        var guide=new Line {Tag="rotation-guide",Stroke=Brushes.SlateGray,IsHitTestVisible=false};
        var handle=CreateRotationHandle();
        Panel.SetZIndex(guide,109);
        Panel.SetZIndex(handle,110);
        EditCanvas.Children.Add(guide);
        EditCanvas.Children.Add(handle);
        PlaceRotationHandle(handle,items);

        History? before=null;
        PdfEditElement[] originals=[];
        Point pivot=new();
        double initialAngle=0;
        bool changed=false;
        Action? cancel=null;
        handle.DragStarted+=(_,_)=>{
            Keyboard.Focus(EditCanvas);
            before=Capture();
            var ids=items.Select(x=>x.Id).ToHashSet();
            originals=before.Plan.Pages[before.Page].Elements.Where(x=>ids.Contains(x.Id)).ToArray();
            var bounds=SelectionBounds(items);
            pivot=new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2);
            initialAngle=PointerAngle(Mouse.GetPosition(EditCanvas),pivot);
            changed=false;
            cancel=()=>{
                var snapshot=before;before=null;
                handle.CancelDrag();
                if(snapshot!=null){Restore(snapshot);SetDirty(!IsClean());}
            };
            cancelEditGesture=cancel;
            CommandManager.InvalidateRequerySuggested();
        };
        handle.DragDelta+=(_,_)=>{
            if(before==null)return;
            var angle=PointerAngle(Mouse.GetPosition(EditCanvas),pivot)-initialAngle;
            if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))angle=Math.Round(angle/15)*15;
            if(!RotateSelectionFrom(items,originals,pivot,(int)Math.Round(angle)))return;
            changed=items.Zip(originals).Any(pair=>pair.First.Rotation!=pair.Second.Rotation ||
                pair.First.X!=pair.Second.X || pair.First.Y!=pair.Second.Y);
            RefreshRotatedObjects(items);
            PlaceRotationHandle(handle,items);
        };
        handle.DragCompleted+=(_,e)=>{
            if(ReferenceEquals(cancelEditGesture,cancel))cancelEditGesture=null;
            var snapshot=before;before=null;
            if(snapshot==null)return;
            if(e.Canceled){Restore(snapshot);SetDirty(!IsClean());}
            else if(changed)Commit(snapshot);
            else DrawElements();
            CommandManager.InvalidateRequerySuggested();
        };
        return handle;
    }

    static double PointerAngle(Point point,Point pivot)
    {
        var direction=point-pivot;
        return Math.Atan2(direction.Y,direction.X)*180/Math.PI;
    }

    static Thumb CreateRotationHandle()
    {
        var handle=new Thumb {Tag="object-rotation",Cursor=Cursors.Hand,Focusable=false,
            ToolTip="끌어서 자유 회전 · Shift로 15도 단위 회전"};
        var circle=new FrameworkElementFactory(typeof(Ellipse));
        circle.SetValue(Shape.FillProperty,Brushes.DarkOrange);
        circle.SetValue(Shape.StrokeProperty,Brushes.Black);
        circle.SetValue(Shape.StrokeThicknessProperty,1.0);
        handle.Template=new ControlTemplate(typeof(Thumb)) {VisualTree=circle};
        System.Windows.Automation.AutomationProperties.SetName(handle,"자유 회전");
        return handle;
    }

    bool RotateSelectionFrom(List<PdfEditElement> items,PdfEditElement[] originals,Point pivot,int delta)
    {
        var turn=new RotateTransform(delta,pivot.X,pivot.Y);
        var candidate=originals.Select(old=>{
            var center=turn.Transform(new Point(old.X+old.Width/2,old.Y+old.Height/2));
            return new PdfEditElement {X=center.X-old.Width/2,Y=center.Y-old.Height/2,
                Width=old.Width,Height=old.Height,Rotation=((old.Rotation+delta)%360+360)%360};
        }).ToArray();
        var bounds=SelectionBounds(candidate);
        var size=SizeOfPage();
        if(bounds.Width>size.W+.000001||bounds.Height>size.H+.000001)return false;
        var dx=Math.Clamp(0,-bounds.Left,Math.Max(-bounds.Left,size.W-bounds.Right));
        var dy=Math.Clamp(0,-bounds.Top,Math.Max(-bounds.Top,size.H-bounds.Bottom));
        for(var i=0;i<items.Count;i++){
            items[i].X=candidate[i].X+dx;
            items[i].Y=candidate[i].Y+dy;
            items[i].Rotation=candidate[i].Rotation;
        }
        return true;
    }

    void RefreshRotatedObjects(List<PdfEditElement> items)
    {
        foreach(var border in EditCanvas.Children.OfType<AnnotationBorder>())
        {
            if(border.Tag is not PdfEditElement item||!items.Contains(item))continue;
            border.RenderTransform=new RotateTransform(item.Rotation);
            Canvas.SetLeft(border,item.X);
            Canvas.SetTop(border,item.Y);
        }
        var grips=EditCanvas.Children.OfType<Thumb>().Where(x=>!Equals(x.Tag,"object-rotation")).ToArray();
        if(grips.Length==2&&Equals(grips[0].Tag,"endpoint")){
            var ends=GraphicEndpoints(items[0]);
            for(var i=0;i<grips.Length;i++)PlaceResizeHandle(grips[i],ends[i]);
        } else if(grips.Length==1)PlaceResizeHandle(grips[0],ResizeCorner(items,SelectionBounds(items)));
    }

    void PlaceRotationHandle(Thumb handle,List<PdfEditElement> items)
    {
        var scale=Math.Max(.01,(EditSurface.LayoutTransform as ScaleTransform)?.ScaleX??1);
        var size=SizeOfPage();
        var guide=EditCanvas.Children.OfType<Line>().First(x=>Equals(x.Tag,"rotation-guide"));
        PositionRotationHandle(handle,guide,items,scale,size.W,size.H);
    }

    void PositionRotationHandle(Thumb handle,Line guide,List<PdfEditElement> items,double scale,double width,double height)
    {
        var bounds=SelectionBounds(items);
        var edge=new Point(bounds.Left+bounds.Width/2,bounds.Top);
        var position=edge+new Vector(0,-24/scale);
        if(items.Count==1){
            var item=items[0];
            var turn=new RotateTransform(item.Rotation,item.X+item.Width/2,item.Y+item.Height/2);
            edge=turn.Transform(new Point(item.X+item.Width/2,item.Y));
            position=turn.Transform(new Point(item.X+item.Width/2,item.Y-24/scale));
        }
        handle.Width=handle.Height=13/scale;
        var radius=handle.Width/2;
        position.X=Math.Clamp(position.X,radius,Math.Max(radius,width-radius));
        position.Y=Math.Clamp(position.Y,radius,Math.Max(radius,height-radius));
        PlaceResizeHandle(handle,position);
        guide.X1=edge.X;guide.Y1=edge.Y;guide.X2=position.X;guide.Y2=position.Y;
        guide.StrokeThickness=1/scale;
    }
}

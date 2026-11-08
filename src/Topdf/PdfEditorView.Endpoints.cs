using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
namespace Topdf;
public partial class PdfEditorView
{
    Point[] GraphicEndpoints(PdfEditElement item)
    {
        var inset=Math.Min(item.StrokeWidth/2,Math.Min(item.Width,item.Height)/2);
        var transform=new RotateTransform(item.Rotation,item.X+item.Width/2,item.Y+item.Height/2);
        return item.Points.Select(p=>transform.Transform(new Point(item.X+inset+p[0]*(item.Width-2*inset),item.Y+inset+p[1]*(item.Height-2*inset)))).ToArray();
    }
    void SetGraphicEndpoints(PdfEditElement item,Point a,Point b)
    {
        var size=SizeOfPage();var margin=Math.Min(item.StrokeWidth/2,Math.Min(size.W,size.H)/2);
        a=new Point(Math.Clamp(a.X,margin,size.W-margin),Math.Clamp(a.Y,margin,size.H-margin));
        b=new Point(Math.Clamp(b.X,margin,size.W-margin),Math.Clamp(b.Y,margin,size.H-margin));
        var pad=Math.Max(item.StrokeWidth,item.Kind=="arrow"?item.StrokeWidth*4:1);
        item.X=Math.Max(0,Math.Min(a.X,b.X)-pad);item.Y=Math.Max(0,Math.Min(a.Y,b.Y)-pad);
        item.Width=Math.Min(size.W,Math.Max(a.X,b.X)+pad)-item.X;
        item.Height=Math.Min(size.H,Math.Max(a.Y,b.Y)+pad)-item.Y;item.Rotation=0;
        var inset=Math.Min(item.StrokeWidth/2,Math.Min(item.Width,item.Height)/2);
        var w=Math.Max(.001,item.Width-2*inset);var h=Math.Max(.001,item.Height-2*inset);
        item.Points=new[]{a,b}.Select(p=>new[]{Math.Clamp((p.X-item.X-inset)/w,0,1),Math.Clamp((p.Y-item.Y-inset)/h,0,1)}).ToList();
    }
    void DrawEndpointHandles(PdfEditElement item)
    {
        var handles=new List<Thumb>();
        void Position(){var points=GraphicEndpoints(item);for(int i=0;i<handles.Count;i++){Canvas.SetLeft(handles[i],points[i].X-5);Canvas.SetTop(handles[i],points[i].Y-5);}}
        for(int index=0;index<2;index++){
            var end=index;var thumb=new Thumb {Width=10,Height=10,Background=Brushes.White,BorderBrush=Brushes.DodgerBlue,BorderThickness=new Thickness(2),Cursor=Cursors.SizeAll,ToolTip=end==0?"시작점 이동":"끝점 이동",Tag="endpoint"};
            History? before=null;Point[] original=[];Vector delta=new();
            thumb.DragStarted+=(_,_)=>{before=Capture();original=GraphicEndpoints(item);delta=new();};
            thumb.DragDelta+=(_,e)=>{
                if(before==null)return;delta+=new Vector(e.HorizontalChange,e.VerticalChange);
                var points=original.ToArray();points[end]+=delta;SetGraphicEndpoints(item,points[0],points[1]);
                foreach(var border in EditCanvas.Children.OfType<Border>().Where(b=>ReferenceEquals(b.Tag,item)).ToArray())EditCanvas.Children.Remove(border);
                DrawGraphic(item);Position();
            };
            thumb.DragCompleted+=(_,e)=>{
                if(before==null)return;
                if(e.Canceled){var old=before.Plan.Pages[before.Page].Elements.Single(x=>x.Id==item.Id);item.X=old.X;item.Y=old.Y;item.Width=old.Width;item.Height=old.Height;item.Rotation=old.Rotation;item.Points=old.Points;DrawElements();}
                else if(delta.LengthSquared>.0001)Commit(before);
                else DrawElements();before=null;
            };
            Panel.SetZIndex(thumb,100);handles.Add(thumb);EditCanvas.Children.Add(thumb);
        }
        Position();
    }
}

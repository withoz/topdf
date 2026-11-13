using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
namespace Topdf;
public partial class PdfEditorView
{
    bool AnnotationHit(PdfEditElement item,Point point) =>
        HitAnnotation(item,point,(EditSurface.LayoutTransform as ScaleTransform)?.ScaleX??1);

    static bool HitAnnotation(PdfEditElement item,Point point,double zoom)
    {
        var unrotate=new RotateTransform(-item.Rotation,item.X+item.Width/2,item.Y+item.Height/2);
        var local=unrotate.Transform(point);local.Offset(-item.X,-item.Y);
        var tolerance=4/Math.Max(.01,zoom);
        if(!Graphics.Contains(item.Kind))return new Rect(0,0,item.Width,item.Height).Contains(local);
        var inset=GraphicInset(item);
        var bounds=new Rect(inset,inset,Math.Max(.1,item.Width-2*inset),Math.Max(.1,item.Height-2*inset));
        Geometry geometry;
        if(item.Kind is "rectangle" or "highlight")geometry=new RectangleGeometry(bounds);
        else if(item.Kind=="ellipse")geometry=new EllipseGeometry(bounds);
        else{
            var points=(item.Points.Count>0?item.Points:[new[]{0.0,0.0},new[]{1.0,1.0}]).Select(p=>new Point(inset+p[0]*(item.Width-2*inset),inset+p[1]*(item.Height-2*inset))).ToArray();
            var path=new StreamGeometry();using(var context=path.Open()){
                context.BeginFigure(points[0],false,false);foreach(var p in points.Skip(1))context.LineTo(p,true,false);
                if(item.Kind=="arrow"&&points.Length>=2){
                    var head=ArrowHead(points[^2],points[^1],item);
                    context.BeginFigure(head[0],false,false);context.LineTo(head[1],true,false);context.LineTo(head[2],true,false);
                }
            }path.Freeze();geometry=path;
        }
        var filled=item.Kind=="highlight"||item.Kind is "rectangle" or "ellipse"&&item.Fill!="none"&&item.FillOpacity>0;
        return filled&&geometry.FillContains(local)||geometry.StrokeContains(new Pen(Brushes.Black,item.StrokeWidth+2*tolerance),local);
    }
    PdfEditElement? AnnotationAt(Point point,bool cycle)
    {
        var hits=Page?.Elements.AsEnumerable().Reverse().Where(x=>AnnotationHit(x,point)).ToArray()??[];
        if(hits.Length==0)return null;
        if(cycle){var current=Array.FindIndex(hits,x=>x.Id==selectedId);return hits[(current+1)%hits.Length];}
        return hits[0];
    }
    void SelectAnnotationAtPointer(object sender,MouseButtonEventArgs e)
    {
        if (inlineText is { } input && !IsViewing && !saving)
        {
            var point = e.GetPosition(input);
            if (!new Rect(input.RenderSize).Contains(point))
            {
                ApplyInlineText();
                Keyboard.Focus(EditCanvas);
                e.Handled = true;
                return;
            }
        }
        if(RegionTool.IsChecked==true||IsViewing||saving||Page==null||Tool is not ("선택" or "본문" or "메모"))return;
        for(var node=e.OriginalSource as DependencyObject;node!=null&&node!=EditCanvas;node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node))
            if(node is Thumb or ButtonBase||node is TextBoxBase&&!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))return;
        SelectHitAnnotation(e.GetPosition(EditCanvas),Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),e);
    }
    void SelectHitAnnotation(Point point,bool cycle,MouseButtonEventArgs e)
    {
        var item=AnnotationAt(point,cycle);
        if(item==null||Tool=="메모"&&item.Kind!="note")return;
        var border=EditCanvas.Children.OfType<Border>().FirstOrDefault(x=>ReferenceEquals(x.Tag,item));
        if(border!=null)ElementMouseDown(border,e);
    }
}

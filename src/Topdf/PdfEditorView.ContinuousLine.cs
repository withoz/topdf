using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace Topdf;
public partial class PdfEditorView
{
    Point? continuousLineStart;
    string? continuousLineKind;
    bool continuousArrowStarted;
    Line? continuousLinePreview;
    Polyline? continuousArrowHead;
    Point LinePoint(Point p)
    {
        var size=SizeOfPage();p=new(Math.Clamp(p.X,0,size.W),Math.Clamp(p.Y,0,size.H));
        if(continuousLineStart is Point start && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            p=Math.Abs(p.X-start.X)>=Math.Abs(p.Y-start.Y)?new(p.X,start.Y):new(start.X,p.Y);
        return p;
    }
    bool ContinueLine(Point point,string kind)
    {
        if(Page==null)return false;
        ApplyInlineText();Keyboard.Focus(EditCanvas);
        var p=LinePoint(point);
        if(continuousLineStart is Point start){
            var scale=EditSurface.LayoutTransform as ScaleTransform;
            var zoom=scale==null?1:Math.Max(.01,Math.Min(Math.Abs(scale.ScaleX),Math.Abs(scale.ScaleY)));
            if((p-start).Length<6/zoom)return true;
            var before=Capture();
            var arrow=kind=="arrow"&&!continuousArrowStarted;
            var item=new PdfEditElement {Kind=arrow?"arrow":"line"};SetObjectStyle(item);
            if(arrow) {
                // Store the shaft tail first so the shared renderer puts the head at the first click.
                SetGraphicEndpoints(item,p,start);
                var ends=GraphicEndpoints(item);
                if((ends[1]-ends[0]).Length<6/zoom)return true;
                p=ends[0];
            } else {
                var pad=Math.Max(1,item.StrokeWidth);var size=SizeOfPage();
                item.X=Math.Max(0,Math.Min(start.X,p.X)-pad);item.Y=Math.Max(0,Math.Min(start.Y,p.Y)-pad);
                item.Width=Math.Max(1,Math.Min(size.W,Math.Max(start.X,p.X)+pad)-item.X);
                item.Height=Math.Max(1,Math.Min(size.H,Math.Max(start.Y,p.Y)+pad)-item.Y);
                item.Points=[new[]{(start.X-item.X)/item.Width,(start.Y-item.Y)/item.Height},new[]{(p.X-item.X)/item.Width,(p.Y-item.Y)/item.Height}];
            }
            Page.Elements.Add(item);selectedIds.Clear();selectedId=item.Id;Commit(before);
            if(arrow)continuousArrowStarted=true;
        }
        continuousLineKind=kind;continuousLineStart=p;PreviewContinuousLine(p);
        EditStatus.Text="다음 점 클릭 · Shift 수평/수직 · Enter/우클릭 끝내기 · Esc 선택";
        return true;
    }
    void PreviewContinuousLine(Point point)
    {
        if(continuousLineStart is not Point start)return;
        var p=LinePoint(point);
        if(continuousLinePreview==null)continuousLinePreview=new Line {IsHitTestVisible=false,Opacity=.65};
        if(!EditCanvas.Children.Contains(continuousLinePreview))EditCanvas.Children.Add(continuousLinePreview);
        continuousLinePreview.X1=start.X;continuousLinePreview.Y1=start.Y;continuousLinePreview.X2=p.X;continuousLinePreview.Y2=p.Y;
        continuousLinePreview.Stroke=(Brush)new BrushConverter().ConvertFromString(ColorValue)!;
        continuousLinePreview.StrokeDashArray=LineDashArray(new PdfEditElement {LineStyle=CurrentLineStyle});
        var width=double.Parse(((System.Windows.Controls.ComboBoxItem)StrokeSize.SelectedItem).Content.ToString()!);
        continuousLinePreview.StrokeThickness=width;
        if(continuousLineKind!="arrow"||continuousArrowStarted){
            if(continuousArrowHead!=null)EditCanvas.Children.Remove(continuousArrowHead);
            return;
        }
        continuousArrowHead??=new Polyline {IsHitTestVisible=false,Opacity=.65};
        if(!EditCanvas.Children.Contains(continuousArrowHead))EditCanvas.Children.Add(continuousArrowHead);
        var arrow=new PdfEditElement {Kind="arrow",StrokeWidth=width};
        SetGraphicEndpoints(arrow,p,start);
        var ends=GraphicEndpoints(arrow);
        var tail=ends[0];var tip=ends[1];
        continuousLinePreview.X1=tail.X;continuousLinePreview.Y1=tail.Y;
        continuousLinePreview.X2=tip.X;continuousLinePreview.Y2=tip.Y;
        var direction=tip-tail;
        continuousArrowHead.Stroke=continuousLinePreview.Stroke;
        continuousArrowHead.StrokeThickness=width;
        continuousArrowHead.Points=direction.Length<.001?new PointCollection():ArrowHead(tail,tip,arrow);

    }
    void StopContinuousLine()
    {
        continuousLineStart=null;continuousLineKind=null;continuousArrowStarted=false;
        if(continuousArrowHead!=null)EditCanvas.Children.Remove(continuousArrowHead);
        continuousArrowHead=null;
        if(continuousLinePreview!=null)EditCanvas.Children.Remove(continuousLinePreview);
        continuousLinePreview=null;
    }
}

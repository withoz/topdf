using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace Topdf;
public partial class PdfEditorView
{
    Point? continuousLineStart;
    Line? continuousLinePreview;
    Point LinePoint(Point p)
    {
        var size=SizeOfPage();p=new(Math.Clamp(p.X,0,size.W),Math.Clamp(p.Y,0,size.H));
        if(continuousLineStart is Point start && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            p=Math.Abs(p.X-start.X)>=Math.Abs(p.Y-start.Y)?new(p.X,start.Y):new(start.X,p.Y);
        return p;
    }
    bool ContinueLine(Point point)
    {
        if(Page==null)return false;
        ApplyInlineText();Keyboard.Focus(EditCanvas);
        var p=LinePoint(point);
        if(continuousLineStart is Point start){
            var scale=EditSurface.LayoutTransform as ScaleTransform;
            var zoom=scale==null?1:Math.Max(.01,Math.Min(Math.Abs(scale.ScaleX),Math.Abs(scale.ScaleY)));
            if((p-start).Length<6/zoom)return true;
            var before=Capture();
            var item=new PdfEditElement {Kind="line"};SetObjectStyle(item);
            var pad=Math.Max(1,item.StrokeWidth);var size=SizeOfPage();
            item.X=Math.Max(0,Math.Min(start.X,p.X)-pad);item.Y=Math.Max(0,Math.Min(start.Y,p.Y)-pad);
            item.Width=Math.Max(1,Math.Min(size.W,Math.Max(start.X,p.X)+pad)-item.X);
            item.Height=Math.Max(1,Math.Min(size.H,Math.Max(start.Y,p.Y)+pad)-item.Y);
            item.Points=[new[]{(start.X-item.X)/item.Width,(start.Y-item.Y)/item.Height},new[]{(p.X-item.X)/item.Width,(p.Y-item.Y)/item.Height}];
            Page.Elements.Add(item);selectedIds.Clear();selectedId=item.Id;Commit(before);
        }
        continuousLineStart=p;PreviewContinuousLine(p);
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
        continuousLinePreview.StrokeThickness=double.Parse(((System.Windows.Controls.ComboBoxItem)StrokeSize.SelectedItem).Content.ToString()!);
    }
    void StopContinuousLine()
    {
        continuousLineStart=null;
        if(continuousLinePreview!=null)EditCanvas.Children.Remove(continuousLinePreview);
        continuousLinePreview=null;
    }
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace Topdf;
public partial class PdfEditorView
{
    Canvas? pdfCaretLayer;
    PdfSourceText? pdfCaretLayout;
    int? pdfDragAnchor;
    bool HasPdfCarets => pdfCaretLayout?.CaretStops is {Length: > 1};
    Point[] PdfCaretPoints()
    {
        if(inlineLiveStops is {} live)return live;
        if(pdfCaretLayout?.CaretStops is not {} stops || Page==null)return [];
        var size=info!.Pages[Page.SourceIndex];
        return stops.Select(p=>{
            double x=p[0],y=p[1],w=size.Width,h=size.Height;
            for(int i=0;i<Page.Rotation/90;i++){(x,y)=(h-y,x);(w,h)=(h,w);}
            return new Point(x,y);
        }).ToArray();
    }
    void AttachPdfCarets(PdfSourceText source)
    {
        pdfCaretLayout=source;pdfDragAnchor=null;
        if(inlineText==null||!HasPdfCarets)return;
        // TextBox still owns keyboard input, IME and undo. PDF coordinates own hit testing and ink.
        inlineText.CaretBrush=Brushes.Transparent;inlineText.SelectionOpacity=0;
        pdfCaretLayer=new Canvas {IsHitTestVisible=false};
        EditCanvas.Children.Add(pdfCaretLayer);
        inlineText.SelectionChanged+=(_,_)=>DrawPdfCarets();
        inlineText.GotKeyboardFocus+=(_,_)=>DrawPdfCarets();
        inlineText.LostKeyboardFocus+=(_,_)=>pdfCaretLayer?.Children.Clear();
        int Nearest(Point point){var points=PdfCaretPoints();return points.Length==0?0:Enumerable.Range(0,points.Length).MinBy(i=>(points[i]-point).LengthSquared);}
        inlineText.PreviewMouseLeftButtonDown+=(_,e)=>{
            if(!HasPdfCarets||inlineText==null)return;
            inlineText.Focus();var index=Math.Min(inlineText.Text.Length,Nearest(e.GetPosition(EditCanvas)));
            pdfDragAnchor=(Keyboard.Modifiers&ModifierKeys.Shift)!=0?inlineText.SelectionStart:index;
            inlineText.Select(Math.Min(pdfDragAnchor.Value,index),Math.Abs(pdfDragAnchor.Value-index));
            inlineText.CaptureMouse();e.Handled=true;DrawPdfCarets();
        };
        inlineText.PreviewMouseMove+=(_,e)=>{
            if(pdfDragAnchor is not {} anchor||inlineText==null||e.LeftButton!=MouseButtonState.Pressed)return;
            var index=Math.Min(inlineText.Text.Length,Nearest(e.GetPosition(EditCanvas)));
            inlineText.Select(Math.Min(anchor,index),Math.Abs(anchor-index));e.Handled=true;
        };
        inlineText.PreviewMouseLeftButtonUp+=(_,e)=>{if(pdfDragAnchor==null)return;pdfDragAnchor=null;inlineText?.ReleaseMouseCapture();e.Handled=true;};
        DrawPdfCarets();
    }
    void DrawPdfCarets()
    {
        if(pdfCaretLayer==null)return;
        pdfCaretLayer.Children.Clear();
        if(inlineText==null||!inlineText.IsKeyboardFocused||!HasPdfCarets)return;
        var points=PdfCaretPoints();if(points.Length==0)return;
        var angle=((pdfCaretLayout!.Rotation+(Page?.Rotation??0))%360)*Math.PI/180;
        var normal=new Vector(-Math.Sin(angle),Math.Cos(angle));
        var size=pdfCaretLayout.FontSize;
        var start=Math.Min(inlineText.SelectionStart,points.Length-1);
        var end=Math.Min(inlineText.SelectionStart+inlineText.SelectionLength,points.Length-1);
        for(int i=start;i<end;i++){
            var a=points[i]-normal*(size*.86);var b=points[i+1]-normal*(size*.86);
            var shape=new Polygon {Fill=new SolidColorBrush(Color.FromArgb(80,100,190,245)),Points=new PointCollection {a,b,b+normal*size,a+normal*size}};
            pdfCaretLayer.Children.Add(shape);
        }
        if(inlineText.SelectionLength==0){
            var point=points[Math.Min(inlineText.CaretIndex,points.Length-1)];
            var top=point-normal*(size*.86);var bottom=point+normal*(size*.14);
            pdfCaretLayer.Children.Add(new Line {X1=top.X,Y1=top.Y,X2=bottom.X,Y2=bottom.Y,Stroke=(Brush)new BrushConverter().ConvertFromString(pdfCaretLayout.Color)!,StrokeThickness=1/Math.Max(.1,EditSurface.LayoutTransform is ScaleTransform scale?scale.ScaleX:1)});
        }
    }
    async Task UpdatePdfCaretsAsync(string image,string text,string sourceId,CancellationToken token)
    {
        var path=System.IO.Path.ChangeExtension(image,".carets.json");if(!File.Exists(path))return;
        var layouts=JsonSerializer.Deserialize<PdfSourceText[]>(await File.ReadAllTextAsync(path,token),new JsonSerializerOptions {PropertyNameCaseInsensitive=true});
        if(token.IsCancellationRequested||inlineText?.Text!=text||selectedSource?.Id!=sourceId)return;
        var source=pdfCaretLayout??selectedSource;
        var match=layouts?.Where(x=>x.Text==text&&x.CaretStops is {Length:>1}).MinBy(x=>Math.Pow(x.BaselineX-source.BaselineX,2)+Math.Pow(x.BaselineY-source.BaselineY,2));
        if(match!=null){pdfCaretLayout=match;DrawPdfCarets();}
    }
}

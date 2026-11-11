using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace Topdf;
public partial class PdfEditorView
{
    Rectangle? selectionBox;
    Point selectionStart;
    HashSet<string> selectionBefore=[];
    ModifierKeys selectionModifiers;
    bool selectionMoved;
    void BeginAreaSelection(Point point,ModifierKeys modifiers)
    {
        ApplyInlineText();selectedSource=null;Keyboard.Focus(EditCanvas);
        selectionStart=point;selectionModifiers=modifiers;selectionBefore=Objects().Select(x=>x.Id).ToHashSet();selectionMoved=false;
        if((modifiers&(ModifierKeys.Control|ModifierKeys.Shift))==0){selectedIds.Clear();selectedId="";}
        selectionBox=new Rectangle {Stroke=Brushes.DodgerBlue,StrokeThickness=1,Fill=new SolidColorBrush(Color.FromArgb(28,50,145,230)),IsHitTestVisible=false};
        Panel.SetZIndex(selectionBox,200);DrawElements();EditCanvas.Children.Add(selectionBox);EditCanvas.CaptureMouse();
    }
    Rect AnnotationBounds(PdfEditElement item)=>new RotateTransform(item.Rotation,item.X+item.Width/2,item.Y+item.Height/2).TransformBounds(new Rect(item.X,item.Y,item.Width,item.Height));
    void UpdateAreaSelection(Point point)
    {
        if(selectionBox==null||Page==null)return;
        var size=SizeOfPage();point=new Point(Math.Clamp(point.X,0,size.W),Math.Clamp(point.Y,0,size.H));
        var zoom=EditSurface.LayoutTransform is ScaleTransform scale?Math.Max(.01,scale.ScaleX):1;
        if((point-selectionStart).Length*zoom<4&&!selectionMoved)return;
        selectionMoved=true;var area=new Rect(selectionStart,point);
        var hits=Page.Elements.Where(x=>area.IntersectsWith(AnnotationBounds(x))).Select(x=>x.Id).ToHashSet();
        selectedIds.Clear();
        if((selectionModifiers&(ModifierKeys.Control|ModifierKeys.Shift))!=0)selectedIds.UnionWith(selectionBefore);
        if(selectionModifiers.HasFlag(ModifierKeys.Control))selectedIds.SymmetricExceptWith(hits);else selectedIds.UnionWith(hits);
        selectedId=selectedIds.FirstOrDefault()??"";DrawElements();
        selectionBox.Width=area.Width;selectionBox.Height=area.Height;Canvas.SetLeft(selectionBox,area.X);Canvas.SetTop(selectionBox,area.Y);EditCanvas.Children.Add(selectionBox);
    }
    bool FinishAreaSelection(bool cancel=false)
    {
        if(selectionBox==null)return false;
        selectionBox=null;
        if(cancel){selectedIds.Clear();selectedIds.UnionWith(selectionBefore);selectedId=selectedIds.FirstOrDefault()??"";}
        EditCanvas.ReleaseMouseCapture();SyncRibbonObjectProperties(Selected);DrawElements();return true;
    }
    void NudgeSelection(double dx,double dy)
    {
        var items=Objects();if(items.Count==0||saving)return;
        var bounds=items.Select(AnnotationBounds).ToArray();var size=SizeOfPage();
        dx=Math.Clamp(dx,-bounds.Min(x=>x.Left),size.W-bounds.Max(x=>x.Right));dy=Math.Clamp(dy,-bounds.Min(x=>x.Top),size.H-bounds.Max(x=>x.Bottom));
        if(Math.Abs(dx)+Math.Abs(dy)<.001)return;
        var before=Capture();foreach(var item in items){item.X+=dx;item.Y+=dy;}Commit(before);
        if(items.Any(x=>x.Kind=="replace"))_ = RenderPageAsync();
    }
}

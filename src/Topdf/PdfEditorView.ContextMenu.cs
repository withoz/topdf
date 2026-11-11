using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace Topdf;
public partial class PdfEditorView
{
    readonly List<string> recentAnnotationColors=[];
    Point annotationContextPoint;
    void PrepareAnnotationContext(Point point,bool pointer)
    {
        CancelPendingEditGesture();FinishAreaSelection(true);ApplyInlineText();annotationContextPoint=point;
        if(pointer){var hit=AnnotationAt(point,false);
            if(hit==null){selectedIds.Clear();selectedId="";selectedSource=null;DrawElements();}
            else if(!Objects().Any(x=>x.Id==hit.Id)){selectedIds.Clear();selectedIds.Add(hit.Id);selectedId=hit.Id;selectedSource=null;SyncRibbonObjectProperties(hit);DrawElements();}
        }
        BuildAnnotationContextMenu();
    }
    void ApplyContextProperty(string property,string value)
    {
        ApplyInlineText();var items=Objects();var before=Capture();
        var wasReady=ribbonReady;ribbonReady=false;
        try{
            if(property=="color"){
                var option=EditColor.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>(string?)x.Tag==value);
                if(option==null){option=new ComboBoxItem{Content=value,Tag=value};EditColor.Items.Add(option);}EditColor.SelectedItem=option;
                recentAnnotationColors.Remove(value);recentAnnotationColors.Insert(0,value);if(recentAnnotationColors.Count>6)recentAnnotationColors.RemoveAt(6);
            }else if(property=="line")LineType.SelectedItem=LineType.Items.Cast<ComboBoxItem>().Single(x=>(string)x.Tag==value);
            else if(property=="width"){
                var option=StrokeSize.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>x.Content.ToString()==value);
                if(option==null){option=new ComboBoxItem{Content=value};StrokeSize.Items.Add(option);}StrokeSize.SelectedItem=option;
            }
        }finally{ribbonReady=wasReady;}
        foreach(var item in items){
            if(property=="color")item.Color=value;
            else if(property=="line"&&Graphics.Contains(item.Kind)){item.LineStyle=value;item.Dashed=value=="dashed";}
            else if(property=="width"&&Graphics.Contains(item.Kind))item.StrokeWidth=double.Parse(value,CultureInfo.InvariantCulture);
            else if(property=="font"&&item.Kind is "text" or "note"){item.FontSize=double.Parse(value,CultureInfo.InvariantCulture);FitTextBox(item,item.Text);}
        }
        if(items.Count>0)Commit(before);
        EditCanvas.ContextMenu.IsOpen=false;
    }
    void BuildAnnotationContextMenu()
    {
        var menu=EditCanvas.ContextMenu;
        foreach(var old in menu.Items.OfType<FrameworkElement>().Where(x=>Equals(x.Tag,"annotation-properties")).ToArray())menu.Items.Remove(old);
        var items=Objects();var graphics=items.Where(x=>Graphics.Contains(x.Kind)).ToArray();var text=items.Where(x=>x.Kind is "text" or "note").ToArray();var insert=0;
        void Add(FrameworkElement item){item.Tag="annotation-properties";menu.Items.Insert(insert++,item);}
        string Label(string title,IEnumerable<string> values){var distinct=values.Distinct().ToArray();return distinct.Length>1?title+" · 혼합":title;}
        var color=new MenuItem{Header=Label(items.Count==0?"기본 색상":"색상",items.Select(x=>x.Color))};
        var palette=new System.Windows.Controls.Primitives.UniformGrid{Columns=6,Margin=new Thickness(2)};
        foreach(var value in recentAnnotationColors.Concat(new[]{"#FF6500","#111111","#E51B23","#FFB900","#32C800","#00AFC8","#1755B5","#7F32B5","#808080","#FFFFFF"}).Distinct()){
            var current=items.Count==0?ColorValue:items.Select(x=>x.Color).Distinct().Count()==1?items[0].Color:null;
            var swatch=new Button{Width=28,Height=28,Margin=new Thickness(2),ToolTip=value,Background=Brushes.White,BorderBrush=current==value?Brushes.DodgerBlue:Brushes.Transparent,BorderThickness=new Thickness(2),Content=new Ellipse{Width=17,Height=17,Fill=(Brush)new BrushConverter().ConvertFromString(value)!,Stroke=Brushes.Gray,StrokeThickness=.5}};
            swatch.Click+=(_,_)=>ApplyContextProperty("color",value);palette.Children.Add(swatch);
        }
        color.Items.Add(new MenuItem{Header=palette,StaysOpenOnClick=true});Add(color);
        var line=new MenuItem{Header=Label(items.Count==0?"기본 선종류":"선종류",graphics.Select(EffectiveLineStyle)),IsEnabled=items.Count==0||graphics.Length>0};
        foreach(var (key,title) in new[]{("solid","실선"),("dotted","점선"),("dashed","파선"),("dashdot","일점쇄선"),("dashdotdot","이점쇄선")}){
            var header=new StackPanel{Orientation=Orientation.Horizontal};header.Children.Add(new System.Windows.Shapes.Path{Data=Geometry.Parse("M0,6 L72,6"),Width=72,Height=12,Stroke=Brushes.DarkSlateGray,StrokeThickness=1.5,StrokeDashArray=LineDashArray(new(){LineStyle=key})});header.Children.Add(new TextBlock{Text=title,Margin=new Thickness(10,0,0,0)});
            var option=new MenuItem{Header=header,IsCheckable=true,IsChecked=graphics.Length>0?graphics.All(x=>EffectiveLineStyle(x)==key):CurrentLineStyle==key};option.Click+=(_,_)=>ApplyContextProperty("line",key);line.Items.Add(option);
        }Add(line);
        var width=new MenuItem{Header=Label(items.Count==0?"기본 선 두께":"선 두께",graphics.Select(x=>x.StrokeWidth.ToString(CultureInfo.InvariantCulture))),IsEnabled=items.Count==0||graphics.Length>0};
        foreach(var value in new[]{"1","2","4","8"}){var option=new MenuItem{Header=value+" pt",IsCheckable=true,IsChecked=graphics.Length>0?graphics.All(x=>x.StrokeWidth==double.Parse(value)):((ComboBoxItem)StrokeSize.SelectedItem).Content.ToString()==value};option.Click+=(_,_)=>ApplyContextProperty("width",value);width.Items.Add(option);}
        var detail=new MenuItem{Header="세부 설정…"};detail.Click+=(_,_)=>{menu.IsOpen=false;Dispatcher.BeginInvoke(new Action(()=>PenOptions.IsChecked=true));};width.Items.Add(new Separator());width.Items.Add(detail);Add(width);
        if(text.Length>0){var size=new MenuItem{Header=Label("글자 크기",text.Select(x=>x.FontSize.ToString(CultureInfo.InvariantCulture)))};foreach(var value in new[]{"10","12","14","18","24","36"}){var option=new MenuItem{Header=value+" pt",IsCheckable=true,IsChecked=text.All(x=>x.FontSize==double.Parse(value))};option.Click+=(_,_)=>ApplyContextProperty("font",value);size.Items.Add(option);}Add(size);
            var fit=new MenuItem{Header="내용에 맞추기"};fit.Click+=(_,_)=>FitSelectedTextToContent();Add(fit);
        }
        if(items.Count>0){var duplicate=new MenuItem{Header="복제",InputGestureText="Ctrl+D"};duplicate.Click+=(_,_)=>{CopyObjects();PasteObjects();};Add(duplicate);
            var below=new MenuItem{Header="아래 객체 선택",IsEnabled=Page!.Elements.Count(x=>AnnotationHit(x,annotationContextPoint))>1};below.Click+=(_,_)=>{var hit=AnnotationAt(annotationContextPoint,true);if(hit!=null){selectedIds.Clear();selectedId=hit.Id;selectedIds.Add(hit.Id);DrawElements();}};Add(below);
        }Add(new Separator());
    }
}

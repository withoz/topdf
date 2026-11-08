using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
namespace Topdf;
public partial class PdfEditorView
{
    bool ribbonReady;
    void InitializeRibbon()
    {
        string[] colors=["#111111","#555555","#808080","#AAAAAA","#D4D4D4","#FFFFFF","#AF126A","#E51B23","#FF6500","#FFAB00","#FFD000","#FFF000","#A6E31C","#24CC00","#00865F","#00A8BD","#1755B5","#4A00B5","#7300C5","#620080","#F7D7C3","#BA936B","#935927","#694333","#F783F5","#FFCE8A","#FFFF85","#88FBA1","#7FD5F9","#BCABF7"];
        foreach(var color in colors) {
            var brush=(Brush)new BrushConverter().ConvertFromString(color)!;
            var button=new RadioButton {Width=34,Height=34,Margin=new Thickness(3),GroupName="PdfPenColor",Tag=color,ToolTip=color,Content=new Ellipse {Width=26,Height=26,Fill=brush,Stroke=Brushes.LightGray,StrokeThickness=.7},IsChecked=color==ColorValue};
            var frame=new FrameworkElementFactory(typeof(Border));frame.Name="ColorFrame";frame.SetValue(Border.CornerRadiusProperty,new CornerRadius(20));frame.SetValue(Border.BorderThicknessProperty,new Thickness(2));frame.SetValue(Border.BorderBrushProperty,Brushes.Transparent);
            var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(HorizontalAlignmentProperty,HorizontalAlignment.Center);presenter.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);frame.AppendChild(presenter);
            var template=new ControlTemplate(typeof(RadioButton)){VisualTree=frame};var trigger=new Trigger {Property=RadioButton.IsCheckedProperty,Value=true};trigger.Setters.Add(new Setter(Border.BorderBrushProperty,new SolidColorBrush(Color.FromRgb(72,146,190)),"ColorFrame"));template.Triggers.Add(trigger);button.Template=template;
            button.Checked+=(_,_)=>SelectPenColor(color);PenPalette.Children.Add(button);
        }
        EditColor.SelectionChanged+=(_,_)=>RefreshPenColor();
        StrokeSize.SelectionChanged+=(_,_)=>{if(double.TryParse((StrokeSize.SelectedItem as ComboBoxItem)?.Content.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var width))PenWidthSlider.Value=Math.Clamp(width,1,8);};
        ribbonReady=true;RefreshPenColor();RibbonSelectionChanged(this,new SelectionChangedEventArgs(SelectorEvent,Array.Empty<object>(),Array.Empty<object>()));
    }
    public IReadOnlyList<RadioButton> RibbonButtons => (EditorRibbon?.Children.OfType<RadioButton>() ?? []).Concat(TextTools?.Children.OfType<RadioButton>() ?? []).Concat(DrawingTools?.Children.OfType<RadioButton>() ?? []).Concat(MarkupTools?.Children.OfType<RadioButton>() ?? []).ToArray();
    static readonly RoutedEvent SelectorEvent=System.Windows.Controls.Primitives.Selector.SelectionChangedEvent;
    void RibbonToolChecked(object sender,RoutedEventArgs e)
    {
        if(EditTool==null || sender is not RadioButton {Tag:string tag} || !int.TryParse(tag,out var index) || index<0)return;
        ApplyInlineText();
        if(index is 2 or 3 && TextFont!=null) {
            foreach(var option in TextFont.Items.Cast<ComboBoxItem>().Where(x=>x.Content.ToString() is not ("Malgun Gothic" or "Gulim" or "Batang")).ToArray())TextFont.Items.Remove(option);
            if(TextFont.SelectedIndex<0)TextFont.SelectedIndex=0;
        }
        EditTool.SelectedIndex=index;
    }
    void RibbonSelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(EditorRibbon==null || EditTool==null)return;
        foreach(var tool in RibbonButtons)tool.IsChecked=int.TryParse(tool.Tag?.ToString(),out var index)&&index==(IsViewing ? -1 : EditTool.SelectedIndex);
        if(TextProperties!=null)TextProperties.Visibility=!IsViewing && EditTool.SelectedIndex is 0 or 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
        var active=new SolidColorBrush(Color.FromRgb(221,239,250));
        if(TextToolsButton!=null)TextToolsButton.Background=!IsViewing && EditTool.SelectedIndex is 0 or 2 ? active : Brushes.White;
        if(DrawingToolsButton!=null)DrawingToolsButton.Background=!IsViewing && EditTool.SelectedIndex is 5 or 6 or 7 or 8 or 12 ? active : Brushes.White;
        if(MarkupToolsButton!=null)MarkupToolsButton.Background=!IsViewing && EditTool.SelectedIndex is 3 or 9 or 10 or 11 ? active : Brushes.White;
    }
    bool syncingPalette;
    void RefreshPenColor()
    {
        syncingPalette=true;
        try {var brush=(Brush)new BrushConverter().ConvertFromString(ColorValue)!;PenColorChip.Fill=brush;PenPreview.Stroke=brush;
            foreach(var option in PenPalette.Children.OfType<RadioButton>())option.IsChecked=(string)option.Tag==ColorValue;
        } finally {syncingPalette=false;}
    }
    bool choosingColor;
    void SelectPenColor(string color)
    {
        if(choosingColor || syncingPalette || !ribbonReady)return;choosingColor=true;
        try {
            var item=EditColor.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>(string?)x.Tag==color);
            if(item==null){item=new ComboBoxItem {Content=color,Tag=color};EditColor.Items.Add(item);}EditColor.SelectedItem=item;
            var objects=Objects();if(objects.Count>0&&!saving){var before=Capture();foreach(var element in objects)element.Color=color;Commit(before);}
        } finally {choosingColor=false;}
    }
    void PenWidthChanged(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(StrokeSize==null || PenPreview==null || PenWidthLabel==null)return;
        var value=e.NewValue.ToString("0.#",CultureInfo.InvariantCulture);PenPreview.StrokeThickness=e.NewValue;PenWidthLabel.Text=value+" pt";
        var item=StrokeSize.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>x.Content.ToString()==value);
        if(item==null){item=new ComboBoxItem {Content=value};StrokeSize.Items.Add(item);}StrokeSize.SelectedItem=item;
        if(!ribbonReady||saving)return;var objects=Objects().Where(x=>Graphics.Contains(x.Kind)).ToArray();if(objects.Length==0)return;
        var before=Capture();foreach(var element in objects)element.StrokeWidth=e.NewValue;Commit(before);
    }
}

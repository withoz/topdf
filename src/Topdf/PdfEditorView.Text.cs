using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Topdf;

public partial class PdfEditorView
{
    public IReadOnlyList<PdfSourceText> SourceTextItems => Page != null && sourceTexts.TryGetValue(Page.SourceIndex,out var items) ? items : [];
    PdfSourceText DisplayText(PdfSourceText item)
    {
        var result=item; var size=info!.Pages[Page!.SourceIndex]; double w=size.Width,h=size.Height;
        for(int turn=0;turn<Page.Rotation/90;turn++) {
            var cx=result.EditX+result.EditWidth/2;var cy=result.EditY+result.EditHeight/2;
            result=result with { X=h-result.Y-result.Height,Y=result.X,Width=result.Height,Height=result.Width,
                EditX=h-cy-result.EditWidth/2,EditY=cx-result.EditHeight/2,Rotation=(result.Rotation+90)%360 };
            (w,h)=(h,w);
        }
        return result;
    }
    PdfSourceText EditingText(PdfSourceText source)
    {
        var item=DisplayText(source);var existing=Page?.Elements.FirstOrDefault(x=>x.SourceTextId==source.Id);
        return existing==null?item:item with {EditX=existing.X,EditY=existing.Y,EditWidth=existing.Width,EditHeight=existing.Height,Rotation=existing.Rotation,FontSize=existing.FontSize,Color=existing.Color,FontFamily=existing.FontFamily};
    }
    void DrawSourceTexts()
    {
        foreach(var original in SourceTextItems) {
            if(Page!.Elements.Any(e=>e.SourceTextId==original.Id)) continue;
            var item=DisplayText(original);
            var box=new Border { Width=item.Width,Height=item.Height,Background=Brushes.Transparent,Cursor=Cursors.IBeam,ToolTip="본문 수정" };
            box.MouseLeftButtonDown+=(_,e)=> {
                if(Tool != "본문" || saving) return;
                SelectSourceText(original.Id); e.Handled=true;
            };
            Canvas.SetLeft(box,item.X);Canvas.SetTop(box,item.Y);EditCanvas.Children.Add(box);
        }
        if(selectedSource==null) return;
        var selected=EditingText(selectedSource);
        inlineText=new TextBox { Text=EditText.Text,Width=selected.EditWidth,Height=selected.EditHeight,
            FontFamily=OriginalFont(selected.FontFamily),FontSize=selected.FontSize,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,
            Foreground=Brushes.Transparent,CaretBrush=(Brush)new BrushConverter().ConvertFromString(selected.Color)!,SelectionBrush=Brushes.LightSkyBlue,SelectionOpacity=.35,SelectionTextBrush=Brushes.Transparent,Padding=new Thickness(0),
            BorderBrush=Brushes.DodgerBlue,BorderThickness=new Thickness(0),Background=Brushes.Transparent,
            RenderTransformOrigin=new Point(.5,.5),RenderTransform=new RotateTransform(selected.Rotation) };
        void ResizeInput() {
            if(inlineText==null) return;
            var layout=ReplacementLayout(selected,inlineText.Text,selected.FontSize,selected.Color);
            inlineText.Width=layout.Width;inlineText.Height=layout.Height;
            Canvas.SetLeft(inlineText,layout.X);Canvas.SetTop(inlineText,layout.Y);
        }
        ResizeInput();
        inlineText.TextChanged+=(_,_)=> { if(inlineText!=null) { EditText.Text=inlineText.Text;ResizeInput();QueueInlinePreview(); } };
        inlineText.PreviewKeyDown+=(_,e)=> {
            if(e.Key==Key.Enter && Keyboard.Modifiers!=ModifierKeys.Shift) { ApplyInlineText();e.Handled=true; }
            else if(e.Key==Key.Escape) { selectedSource=null;DrawElements();e.Handled=true; }
        };
        inlineText.LostKeyboardFocus+=(_,_)=> { if(!selectingText) ApplyInlineText(); };
        Canvas.SetLeft(inlineText,selected.EditX);Canvas.SetTop(inlineText,selected.EditY);EditCanvas.Children.Add(inlineText);AttachPdfCarets(selectedSource);BeginLiveText(selectedSource);
    }
    void SelectFont(double size,string color)
    {
        var label=size.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture);
        var item=EditFontSize.Items.Cast<ComboBoxItem>().FirstOrDefault(c=>c.Content.ToString()==label);
        if(item==null) { item=new ComboBoxItem {Content=label};EditFontSize.Items.Add(item); }
        EditFontSize.SelectedItem=item;
        var colorItem=EditColor.Items.Cast<ComboBoxItem>().FirstOrDefault(c=>(string?)c.Tag==color);
        if(colorItem==null) {colorItem=new ComboBoxItem {Content="원본색",Tag=color};EditColor.Items.Add(colorItem);}
        EditColor.SelectedItem=colorItem;
    }
    public void SelectSourceText(string id)
    {
        EnterEditing();
        ApplyInlineText();
        selectedIds.Clear(); selectedSource=SourceTextItems.Single(t=>t.Id==id);selectedId="";
        var current=EditingText(selectedSource);
        EditText.Text=Page!.Elements.FirstOrDefault(x=>x.SourceTextId==id)?.Text??selectedSource.Text;SelectFont(current.FontSize,current.Color);SelectOriginalFont(current.FontFamily);
        DrawElements();inlineText?.Focus();inlineText?.SelectAll();DrawPdfCarets();
        if(Page.Elements.Any(x=>x.SourceTextId==id))QueueInlinePreview();
        EditStatus.Text="본문을 직접 입력한 뒤 Enter로 적용하세요. Esc는 선택 취소입니다.";
    }
    void ApplyInlineText()
    {
        addedTextCommit?.Invoke();
        if(selectedSource==null || inlineText==null || selectingText || saving) return;
        var text=inlineText.Text;var original=selectedSource;
        if(text==(Page?.Elements.FirstOrDefault(x=>x.SourceTextId==original.Id)?.Text??original.Text)) return;
        inlineText=null;ReplaceSourceText(original.Id,text);
    }
    public void ReplaceSourceText(string id,string text)
    {
        if(Page==null || info==null || saving) return;
        if(text.Length>5000) {EditStatus.Text="본문은 5000자까지 수정할 수 있습니다.";return;}
        var original=SourceTextItems.Single(t=>t.Id==id);var item=EditingText(original);
        var element=ReplacementLayout(item,text,FontSizeValue,ColorValue);
        element.Kind="replace";element.PreserveSourceStyle=true;var family=((ComboBoxItem)TextFont.SelectedItem).Content.ToString()!;if(family!=item.FontFamily){element.FontFamily=family;element.PreserveSourceStyle=false;}element.SourceTextId=id;element.SourceText=original.Text;
        try { Clamp(element); } catch(Exception ex) {EditStatus.Text=ex.Message;return;}
        KeepVisibleInlineDraft();
        var before=Capture();Page.Elements.RemoveAll(e=>e.SourceTextId==id);Page.Elements.Add(element);
        selectedSource=null;selectedId=element.Id;Commit(before);_ = RenderPageAsync();
    }
    PdfEditElement ReplacementLayout(PdfSourceText item,string text,double fontSize,string color)
    {
        var element=new PdfEditElement { Text=text,X=item.EditX,Y=item.EditY,Width=item.EditWidth,Height=item.EditHeight,Rotation=item.Rotation,FontSize=fontSize,Color=color,FontFamily=item.FontFamily,PreserveSourceStyle=true };
        var radians=item.Rotation*Math.PI/180;var dx=Math.Cos(radians);var dy=Math.Sin(radians);
        var px=-dy;var py=dx;
        var anchor=new Point(item.EditX+item.EditWidth/2-dx*item.EditWidth/2-px*item.EditHeight/2,
            item.EditY+item.EditHeight/2-dy*item.EditWidth/2-py*item.EditHeight/2);
        var size=SizeOfPage();
        var maximum=item.Rotation switch {0=>size.W-anchor.X,90=>size.H-anchor.Y,180=>anchor.X,_=>anchor.Y};
        foreach(var other in SourceTextItems.Where(t=>t.Id!=item.Id)) {
            var box=DisplayText(other);var projections=new List<Point>();
            foreach(var x in new[]{box.X,box.X+box.Width})foreach(var y in new[]{box.Y,box.Y+box.Height})
                projections.Add(new Point((x-anchor.X)*dx+(y-anchor.Y)*dy,(x-anchor.X)*px+(y-anchor.Y)*py));
            var left=projections.Min(p=>p.X);
            if(left>item.EditWidth && projections.Min(p=>p.Y)<item.EditHeight && projections.Max(p=>p.Y)>0)maximum=Math.Min(maximum,left-2);
        }
        var formatted=new FormattedText(text,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(OriginalFont(item.FontFamily),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),fontSize,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var width=Math.Max(item.EditWidth,Math.Min(formatted.WidthIncludingTrailingWhitespace+8,maximum-2));
        element.Width=width;var height=Math.Max(item.EditHeight,TextHeight(element));
        var dw=width-item.EditWidth;var dh=height-item.EditHeight;
        element.X+=(dx*dw+px*dh-dw)/2;element.Y+=(dy*dw+py*dh-dh)/2;element.Height=height;
        return element;
    }
}

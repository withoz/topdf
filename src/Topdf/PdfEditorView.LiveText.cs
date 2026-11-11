using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace Topdf;
public partial class PdfEditorView
{
    ImageSource? inlineCleanPage,inlineExactPage;
    Image? inlineLiveInk;
    string inlineExactText="";
    Point[]? inlineLiveStops;
    CancellationTokenSource? inlineBackground;
    void UpdateInlineBasePage(ImageSource image)
    {
        if(inlineText==null||selectedSource==null)return;
        inlineExactPage=image;
        inlineExactText=Page?.Elements.FirstOrDefault(x=>x.SourceTextId==selectedSource.Id)?.Text??selectedSource.Text;
        RestartCleanPage(selectedSource);
        if(inlineText.Text==inlineExactText)DrawLiveText();
        QueueInlinePreview();
    }
    void BeginLiveText(PdfSourceText source)
    {
        inlineCleanPage=null;inlineExactPage=EditPageImage.Source;inlineLiveStops=null;inlineExactText=inlineText?.Text??"";
        inlineLiveInk=new Image {IsHitTestVisible=false,Width=EditCanvas.Width,Height=EditCanvas.Height,Stretch=Stretch.Fill};
        EditCanvas.Children.Add(inlineLiveInk);
        if(pdfCaretLayer!=null)Panel.SetZIndex(pdfCaretLayer,20);
        RestartCleanPage(source);
    }
    void RestartCleanPage(PdfSourceText source)
    {
        inlineBackground?.Cancel();inlineBackground?.Dispose();
        inlineBackground=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _=PrepareLiveTextAsync(source,inlineText,renderVersion,inlineBackground.Token);
    }
    async Task PrepareLiveTextAsync(PdfSourceText source,TextBox? input,long version,CancellationToken token)
    {
        if(input==null||Page==null)return;
        try{
            var copy=PdfEditService.Clone(plan);var page=copy.Pages[EditPages.SelectedIndex];
            var blank=ReplacementLayout(EditingText(source),"",FontSizeValue,ColorValue);
            blank.Kind="replace";blank.SourceTextId=source.Id;blank.SourceText=source.Text;
            page.Elements.RemoveAll(x=>x.SourceTextId==source.Id);page.Elements.Add(blank);
            var image=await PdfEditService.RenderAsync(snapshot,work,page.SourceIndex,page.Rotation,token,copy);
            if(token.IsCancellationRequested||version!=renderVersion||!ReferenceEquals(inlineText,input)||selectedSource?.Id!=source.Id)return;
            inlineCleanPage=LoadImage(image);DrawLiveText();
        }catch(OperationCanceledException){}catch(Exception ex){if(!token.IsCancellationRequested){EditStatus.Visibility=Visibility.Visible;EditStatus.Text=ex.Message;}}
    }
    void DrawLiveText()
    {
        if(inlineText==null||selectedSource==null||inlineLiveInk==null)return;
        if(inlineText.Text==inlineExactText){inlineLiveInk.Source=null;inlineLiveStops=null;if(inlineExactPage!=null)EditPageImage.Source=inlineExactPage;DrawPdfCarets();return;}
        if(inlineCleanPage==null)return;
        var source=DisplayText(selectedSource);var original=selectedSource.CaretStops;
        if(original is not {Length:>1})return;
        // Draw immediately on the text-free page; the PDF engine replaces this draft with exact ink.
        var raw=pdfCaretLayout;pdfCaretLayout=selectedSource;inlineLiveStops=null;
        var positions=PdfCaretPoints();pdfCaretLayout=raw;
        var angle=source.Rotation*Math.PI/180;var along=new Vector(Math.Cos(angle),Math.Sin(angle));var down=new Vector(-along.Y,along.X);
        var drawing=new DrawingGroup();var stops=new List<Point>();
        using(var dc=drawing.Open()){
            var baseline=positions[0];var cursor=baseline;var i=0;var text=inlineText.Text;
            var indexes=StringInfo.ParseCombiningCharacters(text);
            foreach(var index in indexes){
                var next=Array.BinarySearch(indexes,index)+1;var end=next<indexes.Length?indexes[next]:text.Length;var value=text[index..end];
                if(value=="\r"){stops.Add(cursor);continue;}
                if(value=="\n"){stops.Add(cursor);cursor=baseline+down*(++i*source.FontSize*1.35);continue;}
                var formatted=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(OriginalFont(source.FontFamily),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),FontSizeValue,(Brush)new BrushConverter().ConvertFromString(ColorValue)!,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                // Fixed-length replacements keep the exact original glyph origins and kerning.
                if(text.Length==selectedSource.Text.Length&&index<positions.Length)cursor=positions[index];
                for(int c=index;c<end;c++)stops.Add(cursor);
                dc.PushTransform(new RotateTransform(source.Rotation,cursor.X,cursor.Y));
                var origin=new Point(cursor.X,cursor.Y-formatted.Baseline);
                if(source.TextRenderMode is 1 or 2){
                    var ink=(Brush)new BrushConverter().ConvertFromString(ColorValue)!;
                    dc.DrawGeometry(source.TextRenderMode==1?null:ink,new Pen(ink,source.TextStrokeWidth),formatted.BuildGeometry(origin));
                }else dc.DrawText(formatted,origin);
                dc.Pop();
                var advance=formatted.WidthIncludingTrailingWhitespace;
                if(index+1<positions.Length){
                    var oldValue=selectedSource.Text.Substring(Math.Min(index,selectedSource.Text.Length-1),1);
                    var old=new FormattedText(oldValue,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(OriginalFont(source.FontFamily),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),source.FontSize,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    var pdfAdvance=Vector.Multiply(positions[index+1]-positions[index],along);
                    advance=Math.Max(0,advance+pdfAdvance-old.WidthIncludingTrailingWhitespace);
                }
                cursor+=along*advance;
            }
            stops.Add(text.Length==selectedSource.Text.Length?positions[^1]:cursor);
            dc.DrawRectangle(Brushes.Transparent,null,new Rect(0,0,EditCanvas.Width,EditCanvas.Height));
        }
        inlineLiveStops=stops.ToArray();var image=new DrawingImage(drawing);image.Freeze();inlineLiveInk.Source=image;EditPageImage.Source=inlineCleanPage;DrawPdfCarets();
    }
    void KeepVisibleInlineDraft(bool preserveSourceStyle)
    {
        if(!preserveSourceStyle){
            if(inlineCleanPage!=null)EditPageImage.Source=inlineCleanPage;
            return;
        }
        if(inlineLiveInk?.Source is not {} ink||EditPageImage.Source is not {} page)return;
        var combined=new DrawingGroup();using(var dc=combined.Open()){
            var bounds=new Rect(0,0,EditCanvas.Width,EditCanvas.Height);
            dc.DrawImage(page,bounds);dc.DrawImage(ink,bounds);
        }
        var image=new DrawingImage(combined);image.Freeze();EditPageImage.Source=image;
    }
    void AcceptExactInlinePreview(string text,ImageSource image)
    {
        inlineExactPage=image;inlineExactText=text;inlineLiveStops=null;if(inlineLiveInk!=null)inlineLiveInk.Source=null;
    }
}

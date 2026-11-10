using System.Globalization;
using System.Windows;
using System.Windows.Media;
namespace Topdf;
public partial class PdfEditorView
{
    void FitTextBox(PdfEditElement item,string text)
    {
        if(item.Kind is not ("text" or "note"))return;
        var size=SizeOfPage();var pad=item.Kind=="note"?7.0:1.0;
        var available=Math.Max(2*pad+2,size.W-item.X);var minimumWidth=Math.Min(item.Kind=="note"?120:24,available);
        FormattedText Measure(string value)=>new(value.Length==0?" ":value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(item.FontFamily),item.FontSize,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip){LineHeight=item.FontSize*1.35};
        if(item.AutoWidth){var natural=Measure(text);item.Width=Math.Clamp(Math.Ceiling(natural.WidthIncludingTrailingWhitespace)+2*pad+4,minimumWidth,available);}
        else item.Width=Math.Clamp(item.Width,minimumWidth,available);
        var wrapped=Measure(text);wrapped.MaxTextWidth=Math.Max(1,item.Width-2*pad-2);
        // Match the displayed line leading and retain a small border/rounding allowance.
        var needed=Math.Ceiling(wrapped.Height+2*pad+4);
        item.Height=Math.Min(Math.Max(item.Kind=="note"?64:1,needed),Math.Max(1,size.H-item.Y));
        if(needed>size.H-item.Y)EditStatus.Text="글자가 페이지 아래를 벗어납니다. 박스를 위로 옮기거나 내용을 줄여 주세요.";
    }
    void FitSelectedTextToContent()
    {
        ApplyInlineText();var items=Objects().Where(x=>x.Kind is "text" or "note").ToArray();if(items.Length==0)return;
        var before=Capture();foreach(var item in items){item.AutoWidth=true;FitTextBox(item,item.Text);}Commit(before);
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Media;
namespace Topdf;
public partial class PdfEditorView
{
    const double NotePadding = 3;
    static double MinimumNoteWidth(PdfEditElement item) => Math.Ceiling(item.FontSize) + 2 * NotePadding + 2;

    FormattedText MeasureBoxText(PdfEditElement item, string text) =>
        new(string.IsNullOrEmpty(text) ? " " : text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(item.FontFamily), item.FontSize,
            Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip) { LineHeight = item.FontSize * 1.35 };

    double RequiredBoxHeight(PdfEditElement item, string text, double pad)
    {
        var wrapped = MeasureBoxText(item, text);
        wrapped.MaxTextWidth = Math.Max(1, item.Width - 2 * pad - 2);
        return Math.Ceiling(wrapped.Height + 2 * pad + 4);
    }

    void FitTextBox(PdfEditElement item,string text)
    {
        if(item.Kind is not ("text" or "note"))return;
        var note = item.Kind == "note";
        var size=SizeOfPage();var pad=note?NotePadding:1.0;
        var available=Math.Max(2*pad+2,size.W-item.X);var minimumWidth=Math.Min(note?MinimumNoteWidth(item):24,available);
        if(item.AutoWidth){var natural=MeasureBoxText(item,text);item.Width=Math.Clamp(Math.Ceiling(natural.WidthIncludingTrailingWhitespace)+2*pad+4,minimumWidth,available);}
        else item.Width=Math.Clamp(item.Width,minimumWidth,available);
        var needed=RequiredBoxHeight(item,text,pad);
        item.Height=Math.Min(note && item.ManualNoteHeight ? Math.Max(item.Height,needed) : needed,Math.Max(1,size.H-item.Y));
        if(needed>size.H-item.Y)EditStatus.Text="글자가 페이지 아래를 벗어납니다. 박스를 위로 옮기거나 내용을 줄여 주세요.";
    }
    void FitSelectedTextToContent()
    {
        ApplyInlineText();var items=Objects().Where(x=>x.Kind is "text" or "note").ToArray();if(items.Length==0)return;
        var before=Capture();foreach(var item in items){item.AutoWidth=true;item.ManualNoteHeight=false;FitTextBox(item,item.Text);}Commit(before);
    }
}

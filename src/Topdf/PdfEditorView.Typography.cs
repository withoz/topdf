using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace Topdf;
public partial class PdfEditorView
{
    CancellationTokenSource? inlinePreview;
    static readonly Dictionary<string,FontFamily> originalFonts=[];
    static FontFamily OriginalFont(string name)
    {
        if(originalFonts.TryGetValue(name,out var family))return family;
        family=Fonts.SystemFontFamilies.FirstOrDefault(f=>f.Source.Equals(name,StringComparison.OrdinalIgnoreCase)||f.FamilyNames.Values.Any(v=>v.Equals(name,StringComparison.OrdinalIgnoreCase)))??new FontFamily("Malgun Gothic");
        originalFonts[name]=family;return family;
    }
    void SelectOriginalFont(string name)
    {
        var choice=TextFont.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>x.Content.ToString()==name);
        if(choice==null){choice=new ComboBoxItem {Content=name,ToolTip="원본 글꼴"};TextFont.Items.Add(choice);}TextFont.SelectedItem=choice;
    }
    async void QueueInlinePreview()
    {
        inlinePreview?.Cancel();inlinePreview?.Dispose();
        if(selectedSource==null||inlineText==null||Page==null)return;
        inlinePreview=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=inlinePreview.Token;
        var source=selectedSource;var text=inlineText.Text;var page=Page;var version=renderVersion;
        try {
            await Task.Delay(180,token);
            var layout=ReplacementLayout(EditingText(source),text,FontSizeValue,ColorValue);layout.Kind="replace";layout.SourceTextId=source.Id;layout.SourceText=source.Text;var family=((ComboBoxItem)TextFont.SelectedItem).Content.ToString()!;if(family!=source.FontFamily){layout.FontFamily=family;layout.PreserveSourceStyle=false;}
            var copy=PdfEditService.Clone(plan);var target=copy.Pages[EditPages.SelectedIndex];target.Elements.RemoveAll(x=>x.SourceTextId==source.Id);target.Elements.Add(layout);
            string image;
            if(layout.PreserveSourceStyle)image=await PdfEditService.RenderAsync(snapshot,work,page.SourceIndex,page.Rotation,token,copy);
            else {var exported=await PdfEditService.ExportAsync(snapshot,work,copy,token);image=await PdfEditService.RenderAsync(exported,work,EditPages.SelectedIndex,0,token);}
            if(token.IsCancellationRequested||version!=renderVersion||selectedSource?.Id!=source.Id||inlineText?.Text!=text)return;
            EditPageImage.Source=LoadImage(image);
        } catch(OperationCanceledException){} catch(Exception ex){if(!token.IsCancellationRequested){EditStatus.Visibility=Visibility.Visible;EditStatus.Text=ex.Message;}}
    }
}

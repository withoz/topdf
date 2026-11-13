using System.IO;
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
    bool syncingTextFormat;

    void SyncTextFormat(double size,string color,string family,string? alignment)
    {
        syncingTextFormat=true;
        try {
            SelectFont(size,color);
            SelectOriginalFont(family);
            TextAlign.SelectedIndex=alignment switch {"center"=>1,"right"=>2,_=>0};
        } finally {syncingTextFormat=false;}
    }

    void TextFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if(!ribbonReady||syncingTextFormat||Page==null||IsBusy||IsViewing||
            EditFontSize?.SelectedItem==null||TextFont?.SelectedItem is not ComboBoxItem font||
            TextAlign?.SelectedIndex is not (0 or 1 or 2))return;

        var alignment=TextAlign.SelectedIndex switch {1=>"center",2=>"right",_=>"left"};
        if(selectedSource is {} source) {
            ReplaceSourceText(source.Id,inlineText?.Text??EditText.Text,alignment);
            return;
        }

        addedTextCommit?.Invoke();
        var items=Objects().Where(item=>item.Kind is "text" or "note" or "replace").ToList();
        if(items.Count==0)return;
        var before=Capture();
        var changed=false;
        var render=false;
        try {
            foreach(var item in items) {
                var family=ReferenceEquals(sender,TextFont)?font.Content.ToString()!:item.FontFamily;
                var size=ReferenceEquals(sender,EditFontSize)?FontSizeValue:item.FontSize;
                var align=ReferenceEquals(sender,TextAlign)?alignment:item.Alignment;
                if(item.FontFamily==family&&item.FontSize==size&&item.Alignment==align)continue;
                if(item.FontFamily!=family||align!="left")item.PreserveSourceStyle=false;
                item.FontFamily=family;
                item.FontSize=size;
                item.Alignment=align;
                if(item.Kind=="replace") {
                    item.Height=Math.Max(item.Height,TextHeight(item));
                    render=true;
                } else FitTextBox(item,item.Text);
                Clamp(item);
                changed=true;
            }
            if(changed)Commit(before);
            if(render)_=RenderPageAsync();
        } catch(Exception ex){Restore(before);ShowPageOperationError(ex.Message);}
    }

    async void QueueInlinePreview()
    {
        DrawLiveText();
        inlinePreview?.Cancel();inlinePreview?.Dispose();
        if(selectedSource==null||inlineText==null||Page==null)return;
        inlinePreview=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=inlinePreview.Token;
        var source=selectedSource;var input=inlineText;var text=input.Text;var page=Page;var version=renderVersion;
        try {
            await Task.Delay(180,token);
            var layout=ReplacementLayout(EditingText(source),text,FontSizeValue,ColorValue);layout.Kind="replace";layout.SourceTextId=source.Id;layout.SourceText=source.Text;var family=((ComboBoxItem)TextFont.SelectedItem).Content.ToString()!;if(family!=source.FontFamily){layout.FontFamily=family;layout.PreserveSourceStyle=false;}
            var previous=page.Elements.FirstOrDefault(x=>x.SourceTextId==source.Id);
            layout.Alignment=previous?.Alignment??"left";
            layout.PreserveSourceStyle &= previous?.PreserveSourceStyle!=false;
            if(!layout.PreserveSourceStyle)
                layout.Height=Math.Max(layout.Height,TextHeight(layout));
            var copy=PdfEditService.Clone(plan);var target=copy.Pages[EditPages.SelectedIndex];target.Elements.RemoveAll(x=>x.SourceTextId==source.Id);target.Elements.Add(layout);
            string image;
            if(layout.PreserveSourceStyle)image=await PdfEditService.RenderAsync(snapshot,work,page.SourceIndex,page.Rotation,token,copy);
            else {
                // The editor already draws the other annotations on top of the page image.
                target.Elements.RemoveAll(item=>item.Kind!="replace");
                foreach(var other in target.Elements.Where(item=>item!=layout && !item.PreserveSourceStyle))
                    other.Text="";
                copy.Pages=[target];
                var exported=await PdfEditService.ExportAsync(snapshot,work,copy,token);
                try {image=await PdfEditService.RenderAsync(exported,work,0,0,token);}
                finally {File.Delete(exported);}
            }
            if(token.IsCancellationRequested||version!=renderVersion||selectedSource?.Id!=source.Id||!ReferenceEquals(inlineText,input)||input.Text!=text)return;
            var exact=LoadImage(image);AcceptExactInlinePreview(text,exact,layout.PreserveSourceStyle?copy:null);
            EditPageImage.Source=exact;
            await UpdatePdfCaretsAsync(image,text,source.Id,token);
        } catch(OperationCanceledException){} catch(Exception ex){if(!token.IsCancellationRequested){EditStatus.Visibility=Visibility.Visible;EditStatus.Text=ex.Message;}}
    }
}

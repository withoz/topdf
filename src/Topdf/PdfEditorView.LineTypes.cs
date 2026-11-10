using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace Topdf;
public partial class PdfEditorView
{
    bool syncingLineType;
    string CurrentLineStyle=>(LineType?.SelectedItem as ComboBoxItem)?.Tag as string??"solid";
    static string EffectiveLineStyle(PdfEditElement item)=>item.LineStyle??(item.Dashed?"dashed":"solid");
    static DoubleCollection LineDashArray(PdfEditElement item)=>EffectiveLineStyle(item) switch {
        "dotted"=>new(){1,2},"dashed"=>new(){4,3},"dashdot"=>new(){8,3,1,3},"dashdotdot"=>new(){8,3,1,3,1,3},_=>new()
    };
    void SyncLineType(PdfEditElement item)
    {
        if(!Graphics.Contains(item.Kind))return;
        syncingLineType=true;
        try{LineType.SelectedItem=LineType.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>(string)x.Tag==EffectiveLineStyle(item))??LineType.Items[0];}
        finally{syncingLineType=false;}
    }
    void LineTypeChanged(object sender,SelectionChangedEventArgs e)
    {
        if(PenPreview!=null)PenPreview.StrokeDashArray=LineDashArray(new(){LineStyle=CurrentLineStyle});
        if(!ribbonReady||syncingLineType||saving)return;
        var items=Objects().Where(x=>Graphics.Contains(x.Kind)).ToArray();
        if(items.Length==0)return;
        var before=Capture();foreach(var item in items){item.LineStyle=CurrentLineStyle;item.Dashed=CurrentLineStyle=="dashed";}Commit(before);
    }
}

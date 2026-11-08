using System.Globalization;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
namespace Topdf;
public partial class MainWindow
{
    Task<PdfViewingPosition?>? positionRead;
    async Task<PdfViewingPosition?> ReadViewingPositionAsync()
    {
        if(PdfViewer.Visibility!=System.Windows.Visibility.Visible)return null;
        var handle=new WindowInteropHelper(this).Handle;
        var dpi=VisualTreeHelper.GetDpi(PdfViewer).DpiScaleX;
        if(positionRead==null||positionRead.IsCompleted)positionRead=Task.Run(()=>ReadOwnPdfPosition(handle,dpi));
        try{return await positionRead.WaitAsync(TimeSpan.FromSeconds(2));}
        catch(Exception){return null;} // A runtime without PDF accessibility keeps the last editor page.
    }
    static PdfViewingPosition? ReadOwnPdfPosition(IntPtr handle,double dpi)
    {
        // Read only this window's embedded PDF control. Never inspect another application.
        try {
            var window=AutomationElement.FromHandle(handle);if(window.Current.ProcessId!=Environment.ProcessId)return null;
            var viewer=window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"PdfViewer"));
            if(viewer==null)return null;
            var selector=viewer.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"pageselector"));
            if(selector==null||!selector.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern)||!int.TryParse(((ValuePattern)pattern).Current.Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out var page))return null;
            double width=0;
            var groups=viewer.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Group));
            foreach(AutomationElement group in groups) {
                var match=System.Text.RegularExpressions.Regex.Match(group.Current.Name,@"\d+");
                if(!match.Success||!int.TryParse(match.Value,out var number)||number!=page)continue;
                var bounds=group.Current.BoundingRectangle;if(bounds.Width>100){width=bounds.Width/dpi;break;}
            }
            return new(page,width);
        } catch(Exception){return null;}
    }
}

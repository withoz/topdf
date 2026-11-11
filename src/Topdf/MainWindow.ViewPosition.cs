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
    async Task ToggleNativeContentsAsync()
    {
        var handle=new WindowInteropHelper(this).Handle;
        try {await Task.Run(()=>{var root=AutomationElement.FromHandle(handle);if(root.Current.ProcessId!=Environment.ProcessId)return;var viewer=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"PdfViewer"));var button=viewer?.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"contents"));if(button!=null&&button.TryGetCurrentPattern(InvokePattern.Pattern,out var invoke))((InvokePattern)invoke).Invoke();}).WaitAsync(TimeSpan.FromSeconds(2));}catch(Exception){}
    }
    async Task<bool> ChangeNativeZoomAsync(string command)
    {
        var handle=new WindowInteropHelper(this).Handle;
        try{return await Task.Run(()=>{
            var root=AutomationElement.FromHandle(handle);if(root.Current.ProcessId!=Environment.ProcessId)return false;
            var viewer=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"PdfViewer"));
            if(viewer==null)return false;
            var id=command=="in"?"zoom-in":command=="out"?"zoom-out":"pagefit";
            var button=viewer.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,id));
            if(button==null||!button.TryGetCurrentPattern(InvokePattern.Pattern,out var pattern))return false;
            if(command is "width" or "page"){
                var label=button.Current.Name.ToLowerInvariant();
                var offersWidth=label.Contains("너비")||label.Contains("width");
                var offersPage=label.Contains("페이지")||label.Contains("page");
                if((command=="width"&&offersPage)||(command=="page"&&offersWidth)){
                    ((InvokePattern)pattern).Invoke();Thread.Sleep(40);
                    button=viewer.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,id));
                    if(button==null||!button.TryGetCurrentPattern(InvokePattern.Pattern,out pattern))return false;
                }
            }
            ((InvokePattern)pattern).Invoke();return true;
        }).WaitAsync(TimeSpan.FromSeconds(2));}catch(Exception){return false;}
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
            System.Windows.Point? viewportCenter=null;
            var viewport=viewer.Current.BoundingRectangle;
            var groups=viewer.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Group));
            foreach(AutomationElement group in groups) {
                var match=System.Text.RegularExpressions.Regex.Match(group.Current.Name,@"\d+");
                if(!match.Success||!int.TryParse(match.Value,out var number)||number!=page)continue;
                var bounds=group.Current.BoundingRectangle;
                if(bounds.Width<=100)continue;
                width=bounds.Width/dpi;
                if(bounds.Height>0 && viewport.Width>0 && viewport.Height>0)
                    viewportCenter=new((viewport.Left+viewport.Width/2-bounds.Left)/bounds.Width,
                        (viewport.Top+viewport.Height/2-bounds.Top)/bounds.Height);
                break;
            }
            return new(page,width,viewportCenter);
        } catch(Exception){return null;}
    }
}

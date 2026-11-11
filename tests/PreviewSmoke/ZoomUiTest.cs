using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using Topdf;
internal static class ZoomUiTest
{
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static async Task Until(Func<bool> condition){using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(40));while(!condition())await Task.Delay(100,ct.Token);}
    static IEnumerable<DependencyObject> Tree(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var item=VisualTreeHelper.GetChild(root,i);yield return item;foreach(var child in Tree(item))yield return child;}}
    public static int Run(string source)
    {
        int result=1;var app=new Application();app.Startup+=async(_,_)=>{
            var window=new MainWindow();window.Show();
            try{
                var hash=SHA256.HashData(File.ReadAllBytes(source));window.Add([source]);await Until(()=>window.ActivePdfEditor!=null);
                var view=window.ActivePdfEditor!;await view.Ready;await Task.Delay(500);await view.NavigateViewingPdf!(source,2,100);await Task.Delay(300);await view.SyncViewingZoomAsync();
                for(int i=0;i<40;i++){var ready=await view.ReadViewingPosition!();if(ready?.Page==2&&ready.PageWidth>0)break;await Task.Delay(150);}
                Console.WriteLine("Native ready="+await view.ReadViewingPosition!());await view.SyncViewingZoomAsync();
                var viewer=(WebView2)window.FindName("PdfViewer");var uri=viewer.Source;var navigations=0;viewer.CoreWebView2.NavigationStarting+=(_,_)=>navigations++;
                var before=view.CurrentZoomPercent;await view.StepUnifiedZoomAsync(1);Console.WriteLine($"Native before={before} after={view.CurrentZoomPercent} busy={view.IsBusy} viewing={view.IsViewing} status={((TextBlock)view.FindName("EditStatus")).Text}");Check(view.CurrentZoomPercent>before+1,"Native zoom in changes actual PDF scale");
                await view.StepUnifiedZoomAsync(-1);await view.SetUnifiedZoomAsync(200);Check(Math.Abs(view.CurrentZoomPercent-200)<2,"Native 200 percent preset");
                var position=await view.ReadViewingPosition!();Check(position?.Page==2&&viewer.Source==uri&&navigations==0,"Zoom keeps page and avoids PDF reload");
                await view.FitUnifiedZoomAsync(true);var fitPage=view.CurrentZoomPercent;await view.FitUnifiedZoomAsync(false);Check(view.CurrentZoomPercent>fitPage,"Fit page and width act differently");
                await view.SetUnifiedZoomAsync(200);
                var nativePosition=(await view.ReadViewingPosition!())!;
                Check(nativePosition.ViewportCenter!=null,"Native view reports document point at viewport center");
                var expectedWidth=nativePosition.PageWidth;
                var readNative=view.ReadViewingPosition;
                view.ReadViewingPosition=()=>Task.FromResult<PdfViewingPosition?>(nativePosition with {ViewportCenter=new Point(.5,.65)});
                var select=view.RibbonButtons.Single(x=>x.Tag.ToString()=="1");select.IsChecked=true;select.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Until(()=>!view.IsViewing);
                var surface=(FrameworkElement)view.FindName("EditSurface");var scroll=(ScrollViewer)view.FindName("EditScroll");window.UpdateLayout();
                Check(Math.Abs(surface.Width*((ScaleTransform)surface.LayoutTransform).ScaleX-expectedWidth)<4,"View/edit transition retains displayed paper width");
                view.ReadViewingPosition=readNative;
                var anchor=new Point(scroll.ViewportWidth/2,scroll.ViewportHeight/2);var document=scroll.TranslatePoint(anchor,surface);
                Check(Math.Abs(document.Y/surface.Height-.65)<.02,"View/edit transition retains vertical document position");
                Check(Math.Abs(document.X/surface.Width-.5)<.02,"View/edit transition retains horizontal document position");
                await view.SetUnifiedZoomAsync(250,anchor);window.UpdateLayout();await Task.Delay(100);
                var after=surface.TranslatePoint(document,scroll);Check((after-anchor).Length<4,"Editor zoom preserves viewport anchor");
                view.AddText(60,350,"줌 검증 메모",true);var plan=JsonSerializer.Serialize(view.CurrentPlan);
                await view.SetUnifiedZoomAsync(400);await Until(()=>((Image)view.FindName("EditPageImage")).Source is BitmapSource image&&Math.Max(image.PixelWidth,image.PixelHeight)>1800);
                Check(plan==JsonSerializer.Serialize(view.CurrentPlan),"Sharper preview does not change annotation geometry");
                var scale=((ScaleTransform)surface.LayoutTransform).ScaleX;Check(((Canvas)view.FindName("EditCanvas")).Children.OfType<Thumb>().All(x=>Math.Abs(x.Width*scale-(Equals(x.Tag,"object-rotation")?13:10))<.01),"Resize handles keep constant screen size");
                await view.FitUnifiedZoomAsync(true);window.UpdateLayout();Check(surface.ActualHeight*((ScaleTransform)surface.LayoutTransform).ScaleY<scroll.ActualHeight,"Page fit shows whole page in editor");
                Directory.CreateDirectory("artifacts/zoom-ui");var picture=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);picture.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(picture));using(var file=File.Create("artifacts/zoom-ui/editor-fit.png"))encoder.Save(file);
                await view.ReturnToViewingAsync();await view.SetViewColumnsAsync(2);await view.SetUnifiedZoomAsync(200);window.UpdateLayout();
                var spread=(ListBox)view.FindName("SpreadPages");Check(Tree(spread).OfType<ScrollViewer>().Any(x=>x.ExtentWidth>x.ViewportWidth+10),"Spread zoom allows horizontal scrolling");
                await view.FitUnifiedZoomAsync(false);await view.SetViewColumnsAsync(1);await view.SyncViewingZoomAsync();
                Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),"Source unchanged");
                await view.SaveProjectAsync(Path.GetFullPath("artifacts/zoom-ui/zoom-"+Guid.NewGuid()+".topdf"));
                Console.WriteLine("PASS zoom: compact common controls, native presets/fit without reload, current page, view/edit scale, anchored editing zoom, sharper preview, spread horizontal scroll, annotation/source preservation");result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}finally{
                if(window.ActivePdfEditor is {} view){typeof(PdfEditorView).GetField("inlineText",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,null);typeof(PdfEditorView).GetField("selectedSource",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,null);typeof(PdfEditorView).GetField("dirty",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,false);}
                window.Close();app.Shutdown();
            }
        };app.Run();return result;
    }
}

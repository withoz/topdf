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
    public static int RunSpreadScroll(string source)
    {
        int result=1;
        var app=new Application();
        app.Startup+=async (_,_)=>{
            var window=new MainWindow(preferSharedPageEditor:true);
            window.Show();
            try {
                await window.OpenFilesAsync([Path.GetFullPath(source)]);
                var view=window.ActivePdfEditor!;
                await view.Ready;
                var spreads=(ListBox)view.FindName("SpreadPages");
                foreach(var columns in new[]{2,3}){
                    await view.SetViewColumnsAsync(columns);
                    await view.SetUnifiedZoomAsync(200);
                    window.UpdateLayout();
                    var scroll=Tree(spreads).OfType<ScrollViewer>().First();
                    scroll.ScrollToTop();
                    window.UpdateLayout();
                    var wheel=new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,-120){
                        RoutedEvent=System.Windows.Input.Mouse.MouseWheelEvent
                    };
                    scroll.RaiseEvent(wheel);
                    window.UpdateLayout();
                    Check(scroll.VerticalOffset>0&&scroll.VerticalOffset<scroll.ViewportHeight,
                        $"One wheel notch must scroll gradually in {columns}-page view (offset {scroll.VerticalOffset}, viewport {scroll.ViewportHeight})");
                }
                await view.SetViewColumnsAsync(1);
                window.UpdateLayout();
                var single=Tree(spreads).OfType<ScrollViewer>().First();
                await Until(()=>Tree(spreads).OfType<Image>().Count(image=>image.Source!=null)>=2);
                var first=Tree(spreads).OfType<Image>().First();
                Check(view.ViewColumns==1&&spreads.IsVisible&&first.ActualHeight<=single.ViewportHeight&&
                    first.ActualWidth<=single.ViewportWidth,
                    "Returning to one-page view fits the whole page after spread zoom");
                single.ScrollToTop();
                window.UpdateLayout();
                var pageNumber=(TextBox)view.FindName("EditPageNumber");
                single.ScrollToVerticalOffset(first.ActualHeight+40);
                window.UpdateLayout();
                Check(pageNumber.Text=="2"&&first.IsVisible&&first.Source!=null,"Scrolling shows page two and retains loaded page images");
                await view.NavigateToPageAsync(5);
                window.UpdateLayout();
                Check(pageNumber.Text=="5"&&single.VerticalOffset>first.ActualHeight,"Page navigation scrolls to the requested page");
                var sidebar=(FrameworkElement)view.FindName("PageNavigationPanel");
                var thumbnails=(ListBox)view.FindName("EditPages");
                sidebar.Visibility=Visibility.Visible;
                sidebar.Width=300;
                window.UpdateLayout();
                await Until(()=>Tree(thumbnails).OfType<Image>().Count(image=>image.Source!=null)>=2);
                var left=(ListBoxItem)thumbnails.ItemContainerGenerator.ContainerFromIndex(4);
                var right=(ListBoxItem)thumbnails.ItemContainerGenerator.ContainerFromIndex(5);
                var leftPosition=left.TranslatePoint(new Point(),thumbnails);
                var rightPosition=right.TranslatePoint(new Point(),thumbnails);
                Check(Math.Abs(leftPosition.Y-rightPosition.Y)<1&&leftPosition.X<rightPosition.X,
                    "Wide sidebar places adjacent pages on the same row");
                sidebar.Width=195;
                window.UpdateLayout();
                Check(Tree(thumbnails).OfType<VirtualizingStackPanel>().Any(),"Narrow sidebar restores one column");
                Check(thumbnails.SelectedIndex==4,"Resizing preserves page selection");
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var strokeType=typeof(PdfEditorView).GetNestedType("SpreadStroke",BindingFlags.NonPublic)!;
                PdfEditPlan Plan()=>(PdfEditPlan)typeof(PdfEditorView).GetField("plan",flags)!.GetValue(view)!;
                foreach(var columns in new[]{2,3}){
                    await view.NavigateToPageAsync(1);
                    await view.SetViewColumnsAsync(columns);
                    window.UpdateLayout();
                    Tree(spreads).OfType<ScrollViewer>().First().ScrollToTop();
                    window.UpdateLayout();
                    var host=Tree(spreads).OfType<Grid>().First(grid=>grid.ActualWidth>0&&grid.ActualHeight>0&&
                        grid.Children.OfType<Viewbox>().Any(box=>box.Child is Canvas)&&
                        Equals(grid.DataContext?.GetType().GetProperty("Index")?.GetValue(grid.DataContext),columns-1));
                    var overlay=(Canvas)host.Children.OfType<Viewbox>().Single().Child;
                    typeof(PdfEditorView).GetMethod("RefreshSpreadGraphics",flags)!.Invoke(view,[overlay,new RoutedEventArgs()]);
                    var stroke=Activator.CreateInstance(strokeType,host,overlay,host.DataContext,
                        new PdfEditElement {Kind="line"},new List<Point>{new(30,40)})!;
                    Check((bool)typeof(PdfEditorView).GetMethod("UpdateSpreadGraphic",flags)!
                        .Invoke(view,[stroke,new Point(140,150),false])!,$"Spread renders a draft (host {host.ActualWidth}x{host.ActualHeight}, overlay {overlay.Width}x{overlay.Height}, context {overlay.DataContext?.GetType()})");
                    typeof(PdfEditorView).GetMethod("CommitSpreadGraphic",flags)!.Invoke(view,[stroke]);
                    Check(spreads.IsVisible&&view.ViewColumns==columns&&overlay.Children.Count==1,
                        "Drawing stays on the visible spread and renders above the PDF");
                    Check(Plan().Pages[columns-1].Elements.Single().Kind=="line"&&Plan().Pages[0].Elements.Count==0,
                        "Drawing is saved on the targeted page only");
                }
                view.Undo();
                await Until(()=>!view.IsBusy);
                Check(Plan().Pages[2].Elements.Count==0&&spreads.IsVisible,"Undo preserves spread layout");
                view.Redo();
                await Until(()=>!view.IsBusy);
                Check(Plan().Pages[2].Elements.Count==1&&spreads.IsVisible,"Redo preserves spread layout");
                var arrowTool=view.RibbonButtons.Single(button=>Equals(button.Tag,"6"));
                arrowTool.IsChecked=true;
                arrowTool.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(spreads.IsVisible&&arrowTool.IsChecked==true,"Arrow ribbon tool stays active in spread view");
                window.UpdateLayout();
                var firstHost=Tree(spreads).OfType<Grid>().First(grid=>
                    grid.Children.OfType<Viewbox>().Any(box=>box.Child is Canvas)&&
                    Equals(grid.DataContext?.GetType().GetProperty("Index")?.GetValue(grid.DataContext),0));
                var firstOverlay=(Canvas)firstHost.Children.OfType<Viewbox>().Single().Child;
                typeof(PdfEditorView).GetMethod("RefreshSpreadGraphics",flags)!.Invoke(view,[firstOverlay,new RoutedEventArgs()]);
                var complete=typeof(PdfEditorView).GetMethod("CompleteSpreadStroke",flags)!;
                var active=typeof(PdfEditorView).GetField("spreadStroke",flags)!;
                var pending=typeof(PdfEditorView).GetField("spreadLine",flags)!;
                var firstClick=new Point(40,50);
                var chain=Activator.CreateInstance(strokeType,firstHost,firstOverlay,firstHost.DataContext,
                    new PdfEditElement {Kind="arrow"},new List<Point>{firstClick})!;
                foreach(var endpoint in new[]{firstClick,new Point(140,90),new Point(220,120)}){
                    active.SetValue(view,chain);
                    complete.Invoke(view,[chain,endpoint,false]);
                    chain=pending.GetValue(view)!;
                }
                var elements=Plan().Pages[0].Elements;
                Check(elements.Select(element=>element.Kind).SequenceEqual(new[]{"arrow","line"}),
                    "Click chain keeps only one arrowhead");
                var arrow=elements[0];
                Check(Math.Abs(arrow.X+arrow.Points[^1][0]*arrow.Width-firstClick.X)<.01,
                    "Arrowhead stays at the first click");
                typeof(PdfEditorView).GetMethod("CancelSpreadStroke",flags)!.Invoke(view,null);
                var stampFile=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".png");
                var stampEncoder=new PngBitmapEncoder();
                stampEncoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,
                    new byte[]{0,0,255,255},4)));
                using(var stream=File.Create(stampFile))stampEncoder.Save(stream);
                typeof(PdfEditorView).GetField("stamp",flags)!.SetValue(view,stampFile);
                var stampTool=view.RibbonButtons.Single(button=>Equals(button.Tag,"4"));
                stampTool.IsChecked=true;
                stampTool.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(spreads.IsVisible&&stampTool.IsChecked==true,"Stamp tool stays active in spread view");
                typeof(PdfEditorView).GetMethod("PlaceSpreadStamp",flags)!
                    .Invoke(view,[firstHost,firstOverlay,firstHost.DataContext,new Point(60,180)]);
                Check(Plan().Pages[0].Elements.Last().Kind=="stamp"&&
                    firstOverlay.Children.OfType<Image>().Any(image=>image.Source!=null)&&spreads.IsVisible,
                    "Stamp appears on the clicked page without leaving spread view");
                typeof(PdfEditorView).GetMethod("SelectSpreadObject",flags)!.Invoke(view,
                    [firstHost,firstOverlay,firstHost.DataContext,new Point(100,130),System.Windows.Input.ModifierKeys.None]);
                var fields=(StackPanel)view.FindName("ObjectPropertiesFields");
                var rotation=fields.Children.OfType<StackPanel>().SelectMany(row=>row.Children.OfType<ComboBox>())
                    .Single(box=>System.Windows.Automation.AutomationProperties.GetName(box)=="회전");
                Check(firstOverlay.Children.OfType<Border>().Any(),"Spread selection draws an outline");
                var handle=firstOverlay.Children.OfType<Thumb>().Single(thumb=>Equals(thumb.Tag,"object-rotation"));
                Check(handle.IsHitTestVisible&&handle.Width>0,"Spread selection exposes an interactive rotation handle");
                handle.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
                handle.RaiseEvent(new DragCompletedEventArgs(0,0,true){RoutedEvent=Thumb.DragCompletedEvent});
                Check(Plan().Pages[0].Elements.Last().Rotation==0,"Canceling rotation preserves the stamp");
                var resize=firstOverlay.Children.OfType<Thumb>().Single(thumb=>Equals(thumb.Tag,"spread-resize"));
                Check(resize.IsHitTestVisible&&resize.Width>0,"Spread selection exposes a resize handle");
                var before=typeof(PdfEditorView).GetMethod("Capture",flags)!.Invoke(view,null)!;
                var stamps=new List<PdfEditElement>{Plan().Pages[0].Elements.Last()};
                var originals=(PdfEditElement[])typeof(PdfEditorView).GetMethod("SpreadOriginals",flags)!
                    .Invoke(view,[before,stamps])!;
                var moveType=typeof(PdfEditorView).GetNestedType("SpreadMove",BindingFlags.NonPublic)!;
                var move=Activator.CreateInstance(moveType,firstHost,firstOverlay,new Point(100,130),before,stamps,originals)!;
                typeof(PdfEditorView).GetMethod("MoveSpreadObjects",flags)!.Invoke(view,[move,new Point(125,150)]);
                Check(Math.Abs(stamps[0].X-originals[0].X-25)<.01&&
                    firstOverlay.Children.OfType<Image>().Any(image=>Math.Abs(Canvas.GetLeft(image)-stamps[0].X)<.01),
                    "Spread move updates the stamp preview during the gesture");
                typeof(PdfEditorView).GetMethod("ResizeSelectionFrom",flags)!.Invoke(view,[stamps,originals,new Vector(30,30)]);
                Check(stamps[0].Width>originals[0].Width,"Shared resize geometry grows the spread stamp");
                var highlight=new PdfEditElement {Kind="highlight",X=100,Y=100,Width=80,Height=12,StrokeWidth=2};
                var highlightOriginal=new PdfEditElement {Id=highlight.Id,Kind="highlight",X=100,Y=100,Width=80,Height=12,StrokeWidth=2};
                var resizeSelection=typeof(PdfEditorView).GetMethod("ResizeSelectionFrom",flags)!;
                resizeSelection.Invoke(view,[new List<PdfEditElement>{highlight},new[]{highlightOriginal},new Vector(30,40)]);
                Check(highlight.Width==110&&highlight.Height==12&&highlight.StrokeWidth==2&&highlight.Y==100,
                    "Resizing a highlight changes only its length");
                highlight.Rotation=highlightOriginal.Rotation=90;
                resizeSelection.Invoke(view,[new List<PdfEditElement>{highlight},new[]{highlightOriginal},new Vector(40,30)]);
                Check(Math.Abs(highlight.Width-110)<.01&&highlight.Height==12&&highlight.StrokeWidth==2,
                    "A rotated highlight resizes along its length");
                await (Task)typeof(PdfEditorView).GetMethod("CompleteSpreadTransformAsync",flags)!
                    .Invoke(view,[before,stamps,originals,true])!;
                Check(stamps[0].X==originals[0].X&&stamps[0].Width==originals[0].Width,
                    "Canceled transforms restore the original stamp bounds");
                rotation=fields.Children.OfType<StackPanel>().SelectMany(row=>row.Children.OfType<ComboBox>())
                    .Single(box=>System.Windows.Automation.AutomationProperties.GetName(box)=="회전");
                rotation.SelectedItem=rotation.Items.OfType<ComboBoxItem>().Single(item=>Equals(item.Tag,"90"));
                await Until(()=>!view.IsBusy);
                Check(spreads.IsVisible&&view.ViewColumns==3&&Plan().Pages[0].Elements.Last().Rotation==90,
                    "Docked properties apply to the selected spread object");
                var pageObjects=Plan().Pages[0].Elements;
                var existingStamp=pageObjects.Last();
                pageObjects.Add(new PdfEditElement {
                    Kind="rectangle",X=existingStamp.X+10,Y=existingStamp.Y+10,
                    Width=45,Height=45,Fill="#1755B5"
                });
                pageObjects.Add(new PdfEditElement {
                    Kind="note",X=existingStamp.X+30,Y=existingStamp.Y+30,Width=110,Height=55,Text="Layer order"
                });
                var textBox=new PdfEditElement {
                    Kind="text",Text="Text box",X=230,Y=260,Width=130,Height=46,
                    TextBoxFill="#FFB900",TextBoxBorderColor="#1755B5",TextBoxBorderWidth=2,FillOpacity=.65
                };
                pageObjects.Add(textBox);
                foreach(var columns in new[]{2,3}){
                    await view.SetViewColumnsAsync(columns);
                    window.UpdateLayout();
                    var pageHost=Tree(spreads).OfType<Grid>().First(grid=>
                        grid.Children.OfType<Viewbox>().Any(box=>box.Child is Canvas)&&
                        Equals(grid.DataContext?.GetType().GetProperty("Index")?.GetValue(grid.DataContext),0));
                    var stampOverlay=(Canvas)pageHost.Children.OfType<Viewbox>().Single().Child;
                    var existing=Plan().Pages[0].Elements.Single(item=>item.Kind=="stamp");
                    typeof(PdfEditorView).GetMethod("RefreshSpreadGraphics",flags)!
                        .Invoke(view,[stampOverlay,new RoutedEventArgs()]);
                    var stampImage=stampOverlay.Children.OfType<Image>().Single(image=>ReferenceEquals(image.Tag,existing));
                    Check(stampOverlay.Children.OfType<FrameworkElement>().Select(element=>element.Tag)
                        .OfType<PdfEditElement>().Select(item=>item.Id)
                        .SequenceEqual(Plan().Pages[0].Elements.Select(item=>item.Id)),
                        $"Stamp, shape and note use PDF export order in {columns}-page view");
                    var boxVisual=stampOverlay.Children.OfType<Border>().Single(border=>ReferenceEquals(border.Tag,textBox));
                    Check(boxVisual.Background is SolidColorBrush {Opacity:.65},
                        "Text box fill is visible in spread preview");
                    Check(((RotateTransform)stampImage.RenderTransform).Angle==90,
                        $"Existing stamp remains separate from the {columns}-page background");
                    existing.Rotation=135;
                    typeof(PdfEditorView).GetMethod("UpdateSpreadTransformPreview",flags)!.Invoke(view,[stampOverlay]);
                    Check(((RotateTransform)stampImage.RenderTransform).Angle==135,
                        $"Existing stamp image rotates immediately in {columns}-page view");
                    existing.Rotation=90;
                }
                var exported=await view.GetViewingPdfAsync();
                Check(File.Exists(exported),"Spread edits export as PDF");
                var exportedInfo=await PdfEditService.InspectAsync(exported,Path.GetDirectoryName(exported)!,CancellationToken.None);
                Check(exportedInfo.Pages[0].Notes.Any(note=>note.Text=="Layer order"),"An exported note can be read when the PDF reopens");
                typeof(PdfEditorView).GetMethod("SetDirty",flags)!.Invoke(view,[false]);
                Console.WriteLine("PASS: continuous one-, two- and three-page scrolling, single-page fit and page navigation.");
                result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}
            finally{
                if(window.ActivePdfEditor is {} activeView)
                    typeof(PdfEditorView).GetField("dirty",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(activeView,false);
                window.Close();app.Shutdown();
            }
        };
        app.Run();
        return result;
    }
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

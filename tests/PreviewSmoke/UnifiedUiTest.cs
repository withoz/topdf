using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using Topdf;
internal static class UnifiedUiTest
{
    static void Check(bool value,string text){if(!value)throw new Exception(text);}
    static async Task Until(Func<bool> value){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));while(!value())await Task.Delay(100,timeout.Token);}
    public static int Run(string input)
    {
        var result=1;var app=new Application();app.Startup+=async(_,_)=>{
            var window=new MainWindow();window.Show();
            try {
                var source=Path.GetFullPath(input);var hash=SHA256.HashData(File.ReadAllBytes(source));window.Add([source]);
                await Until(()=>window.ActivePdfEditor!=null);var editor=window.ActivePdfEditor!;await editor.Ready.WaitAsync(TimeSpan.FromSeconds(90));
                window.UpdateLayout();
                var tabs=(ListBox)window.FindName("DocumentTabs");var ribbon=(StackPanel)editor.FindName("EditorRibbon");
                Check(tabs.TransformToAncestor(window).Transform(new Point()).Y>ribbon.TransformToAncestor(window).Transform(new Point()).Y,"Document tabs below ribbon");
                var viewer=(WebView2)window.FindName("PdfViewer");Check(editor.IsViewing&&viewer.Visibility==Visibility.Visible,"Initially viewing with ribbon");Check(((Button)window.FindName("EditPdfButton")).Visibility==Visibility.Collapsed,"No separate edit button");
                var tool=editor.RibbonButtons.Single(x=>x.Tag.ToString()=="5");tool.IsChecked=true;tool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Until(()=>!editor.IsViewing);Check(!editor.IsViewing&&viewer.Visibility==Visibility.Collapsed,"Tool click immediately enables edit");
                editor.AddText(60,350,"통합 화면 수정 321,000원");
                var before=System.Text.Json.JsonSerializer.Serialize(editor.CurrentPlan);
                editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(editor),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                Check(!editor.IsViewing&&((ComboBox)editor.FindName("EditTool")).SelectedIndex==1,"First Escape returns drawing tool to selection");
                editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(editor),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                Check(!editor.IsViewing&&editor.RibbonButtons.Single(x=>x.IsChecked==true).Tag.ToString()=="1","Repeated Escape stays in selection");
                Check(before==System.Text.Json.JsonSerializer.Serialize(editor.CurrentPlan),"Escape preserves plan");
                Check(editor.FindName("ViewTool")==null,"Redundant view icon removed");
                foreach(var tag in new[]{"0","2","3","4","6","7","8","9","10","11","12"}) {
                    editor.RibbonButtons.Single(x=>x.Tag.ToString()==tag).IsChecked=true;
                    editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(editor),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});
                    Check(!editor.IsViewing&&((ComboBox)editor.FindName("EditTool")).SelectedIndex==1,"Escape returns tool "+tag+" to selection");
                }
                await editor.ReturnToViewingAsync();
                var shown=viewer.Source!.LocalPath;Check(shown!=source&&File.Exists(shown),"Modified PDF visible before saving");
                var texts=await PdfEditService.TextAsync(shown,Path.GetDirectoryName(shown)!,0,CancellationToken.None);Check(texts.Any(x=>x.Text.Contains("321,000")),"Native preview contains edit");
                tool.IsChecked=true;tool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Until(()=>!editor.IsViewing);Check(!editor.IsViewing&&editor.CurrentPlan.Pages[0].Elements.Count==1,"Same tool resumes unsaved edit");
                editor.Undo();Check(editor.CurrentPlan.Pages[0].Elements.Count==0,"History retained after Escape");editor.Redo();
                await editor.ReturnToViewingAsync();
                await editor.NavigateViewingPdf!(viewer.Source!.LocalPath,2,100);
                using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30)))while((await editor.ReadViewingPosition!())?.Page!=2)await Task.Delay(100,timeout.Token);
                tool.IsChecked=true;tool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Until(()=>!editor.IsViewing);
                Check(((ListBox)editor.FindName("EditPages")).SelectedIndex==1,"Native current page follows into editing");
                var folder=Path.GetFullPath("artifacts/unified-test");Directory.CreateDirectory(folder);var project=Path.Combine(folder,Guid.NewGuid()+".topdf");await editor.SaveProjectAsync(project);
                window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var image=File.Create(Path.Combine(folder,"unified-editor.png")))encoder.Save(image);
                await editor.ReturnToViewingAsync();
                var other=Path.Combine(folder,Guid.NewGuid()+".pdf");File.Copy(source,other);await window.OpenFilesAsync([other]);await Until(()=>window.ActivePdfEditor!=null&&!ReferenceEquals(window.ActivePdfEditor,editor));await window.ActivePdfEditor!.Ready;
                Check(window.ActivePdfEditor.IsViewing,"Tab switching remains available");
                await window.OpenFilesAsync([project]);await Until(()=>window.ActivePdfEditor?.CurrentPlan.Pages.FirstOrDefault()?.Elements.Count==1);await window.ActivePdfEditor!.Ready;
                Check(window.ActivePdfEditor.IsViewing,"Editable project opens in unified view");
                Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),"Original preserved");Check(Application.Current.Windows.Count==1,"Single window");
                var editWindow=new MainWindow(preferSharedPageEditor:true);editWindow.Show();
                try {
                    await editWindow.OpenFilesAsync([source]);
                    await Until(()=>editWindow.ActivePdfEditor is {IsViewing:false});
                    var shared=editWindow.ActivePdfEditor!;
                    var mainMenu=(Menu)editWindow.FindName("MainMenu");
                    Check(mainMenu.Items.Count==5,"Standard menu contains five categories");
                    var windowMenu=(MenuItem)mainMenu.Items[3];
                    windowMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
                    Check(windowMenu.Items.OfType<MenuItem>().Any(item=>item.ToolTip as string==source),"Window menu lists open PDF");
                    var fileMenu=(MenuItem)mainMenu.Items[0];
                    fileMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));
                    var recent=(MenuItem)editWindow.FindName("RecentFileMenu");
                    Check(recent.Items.OfType<MenuItem>().Any(item=>item.ToolTip as string==source),"File menu lists recent PDF");
                    Check(ReferenceEquals(((MenuItem)editWindow.FindName("MenuSaveProject")).CommandTarget,shared.FindName("EditCanvas")),"File menu saves in active editor");
                    await shared.Ready;
                    await Until(()=>((ComboBox)shared.FindName("EditTool")).SelectedIndex==1&&!shared.IsViewing);
                    await shared.NavigateToPageAsync(2);
                    var image=(Image)shared.FindName("EditPageImage");
                    await Until(()=>image.Source!=null&&image.IsVisible);
                    await shared.SetUnifiedZoomAsync(125);
                    var scroll=(ScrollViewer)shared.FindName("EditScroll");
                    var wheelPages=(ListBox)shared.FindName("EditPages");
                    void Wheel(int delta)=>scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,0,delta) {
                        RoutedEvent=Mouse.PreviewMouseWheelEvent
                    });
                    scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);editWindow.UpdateLayout();
                    Wheel(-120);
                    await Until(()=>wheelPages.SelectedIndex==2&&image.IsVisible);
                    Check(scroll.VerticalOffset<1,"Forward wheel opens next page at top");
                    Wheel(120);
                    await Until(()=>wheelPages.SelectedIndex==1&&image.IsVisible);
                    await Until(()=>Math.Abs(scroll.VerticalOffset-scroll.ScrollableHeight)<1);

                    scroll.ScrollToVerticalOffset(85);editWindow.UpdateLayout();
                    var offset=scroll.VerticalOffset;
                    var pageImage=image.Source;
                    var native=(WebView2)editWindow.FindName("PdfViewer");
                    var nativeUri=native.Source;
                    await shared.ReturnToViewingAsync();
                    Check(shared.IsViewing&&image.IsVisible&&native.Visibility!=Visibility.Visible,"Eye mode keeps the editing surface visible");
                    var line=shared.RibbonButtons.Single(x=>x.Tag.ToString()=="5");
                    line.IsChecked=true;line.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Check(!shared.IsViewing&&ReferenceEquals(pageImage,image.Source)&&Math.Abs(offset-scroll.VerticalOffset)<.01&&shared.CurrentZoomPercent==125,
                        "Line tool preserves the exact page image, scroll and zoom");
                    var begin=typeof(PdfEditorView).GetMethod("BeginGraphic",BindingFlags.NonPublic|BindingFlags.Instance)!;
                    begin.Invoke(shared,[new Point(50,80)]);begin.Invoke(shared,[new Point(170,95)]);
                    var textTool=shared.RibbonButtons.Single(x=>x.Tag.ToString()=="2");
                    textTool.IsChecked=true;textTool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    shared.AddText(70,150,"같은 화면 편집");
                    await shared.ReturnToViewingAsync();
                    Check(shared.CurrentPlan.Pages[1].Elements.Count==2&&ReferenceEquals(pageImage,image.Source)&&native.Source==nativeUri,
                        "Line and text remain visible in place without reopening the native PDF");
                    await shared.SetViewColumnsAsync(1);
                    Check(native.CoreWebView2==null&&native.Visibility!=Visibility.Visible&&shared.IsViewing,"Editor opens and switches modes without initializing a separate viewer");
                    var pane=(DockPanel)shared.FindName("PageNavigationPanel");
                    var resize=(System.Windows.Controls.Primitives.Thumb)shared.FindName("PagePaneResize");
                    var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                    object? Invoke(string name,params object[] args)=>typeof(PdfEditorView).GetMethod(name,flags)!.Invoke(shared,args);
                    Invoke("TogglePagePane",shared,new RoutedEventArgs());editWindow.UpdateLayout();
                    var width=pane.Width;
                    resize.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(40,0){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragDeltaEvent});
                    Check(resize.IsVisible&&pane.Width>width+39&&ReferenceEquals(pane.Background,scroll.Background),"Page pane drag and shared background");
                    editWindow.Width=760;editWindow.UpdateLayout();
                    var ribbonScroll=(ScrollViewer)shared.FindName("RibbonScroll");
                    var tools=(StackPanel)shared.FindName("EditorHistory");
                    Check(ReferenceEquals(tools.Parent,shared.FindName("EditorRibbon"))&&ribbonScroll.ScrollableWidth>0,
                        "Editing tools stay in the same ribbon row and scroll on a narrow window");
                    var toggle=typeof(MainWindow).GetMethod("ToggleFullscreen",flags)!;
                    await shared.SetUnifiedZoomAsync(40);
                    var windowedZoom=shared.CurrentZoomPercent;
                    var size=new Size(editWindow.ActualWidth,editWindow.ActualHeight);
                    toggle.Invoke(editWindow,[editWindow,new RoutedEventArgs()]);editWindow.UpdateLayout();
                    await Until(()=>shared.CurrentZoomPercent>windowedZoom+1);
                    toggle.Invoke(editWindow,[editWindow,new RoutedEventArgs()]);editWindow.UpdateLayout();
                    await Until(()=>Math.Abs(shared.CurrentZoomPercent-windowedZoom)<.1);
                    Check(Math.Abs(editWindow.ActualWidth-size.Width)<3&&Math.Abs(editWindow.ActualHeight-size.Height)<3,"F11 restores original size");

                    Invoke("OpenPageSearch");
                    ((TextBox)shared.FindName("PageSearchQuery")).Text="같은 화면 편집";
                    await (Task)Invoke("MovePageSearchAsync",1)!;
                    Check(((TextBlock)shared.FindName("PageSearchNotice")).Text=="1/1","Inline PDF find locates added text");
                    var pageList=(ListBox)shared.FindName("EditPages");
                    Check(pageList.SelectedIndex==1,"Find selects correct page");
                    Invoke("ClosePageSearch",shared,new RoutedEventArgs());

                    var originalOrder=shared.CurrentPlan.Pages.Select(p=>p.SourceIndex).ToArray();
                    await shared.ImportPdfPagesAsync(source,1);
                    Check(shared.CurrentPlan.Pages.Count==originalOrder.Length*2&&shared.HasUnsavedEdits,"PDF insert adds pages and marks the tab dirty");
                    shared.Undo();
                    Check(shared.CurrentPlan.Pages.Select(p=>p.SourceIndex).SequenceEqual(originalOrder)&&shared.CurrentPlan.Pages[1].Elements.Count==2,
                        "Undo insertion restores original pages and annotations");
                    shared.Redo();
                    pageList.SelectedItems.Clear();
                    pageList.SelectedItems.Add(pageList.Items[1]);pageList.SelectedItems.Add(pageList.Items[2]);
                    Invoke("DeleteSelectedPages",shared,new RoutedEventArgs());
                    Check(shared.CurrentPlan.Pages.Count==originalOrder.Length*2-2,"Multiple pages delete in one action");
                    shared.Undo();
                    Check(shared.CurrentPlan.Pages.Count==originalOrder.Length*2,"One Undo restores all deleted pages");
                    var recovery=(string)typeof(PdfEditorView).GetField("recoveryFile",flags)!.GetValue(shared)!;
                    Invoke("SaveRecoveryTick",shared,EventArgs.Empty);
                    await Until(()=>File.Exists(recovery));
                    var expanded=Path.Combine(folder,Guid.NewGuid()+".topdf");
                    await shared.SaveProjectAsync(expanded);
                    Check(!shared.HasUnsavedEdits&&!File.Exists(recovery),"Saving the project clears dirty state and recovery");
                    await shared.LoadProjectAsync(expanded);
                    Check(shared.CurrentPlan.Pages.Count==originalOrder.Length*2,"Merged source and annotations survive project reload");
                } finally {
                    if(editWindow.ActivePdfEditor is {} active)typeof(PdfEditorView).GetField("dirty",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(active,false);
                    editWindow.Close();
                }
                Console.WriteLine("PASS unified UI: editor-first shared image/zoom/scroll, lines and text on same surface, no viewer in editor mode, persistent compact ribbon, direct tool activation, Escape stays in selection, native preview with unsaved edits, same-tool resume, undo/redo retained, PDF/project/tab navigation, single window, source unchanged");result=0;
            } catch(Exception ex){Console.Error.WriteLine(ex);}
            finally{if(window.ActivePdfEditor is {} editor)typeof(PdfEditorView).GetField("dirty",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(editor,false);window.Close();app.Shutdown();}
        };app.Run();return result;
    }
}

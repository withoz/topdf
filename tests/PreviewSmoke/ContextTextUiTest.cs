using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Topdf;
internal static class ContextTextUiTest
{
    static object? Call(PdfEditorView view,string name,params object[] args)=>typeof(PdfEditorView).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,args);
    static object? Field(PdfEditorView view,string name)=>typeof(PdfEditorView).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view);
    static void Set(PdfEditorView view,string name,object? value)=>typeof(PdfEditorView).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,value);
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static int History(PdfEditorView view)=>((ICollection)Field(view,"undo")!).Count;
    public static int Run(string input)
    {
        var result=1;var app=new Application();app.Startup+=async(_,_)=>{
            var window=new PdfEditorWindow(Path.GetFullPath(input));window.Show();var view=window.View;
            try{
                await window.Ready.WaitAsync(TimeSpan.FromSeconds(90));var original=SHA256.HashData(File.ReadAllBytes(input));
                var tool=(ComboBox)view.FindName("EditTool");tool.SelectedIndex=1;var canvas=(Canvas)view.FindName("EditCanvas");
                view.AddText(60,80,"안녕하세요");var item=((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Last();var id=item.Id;var initialWidth=item.Width;var initialHeight=item.Height;
                Check(item.AutoWidth&&initialWidth<160,"Short text starts compact and auto-sized");
                Call(view,"BeginAddedText");await Task.Delay(150);var box=canvas.Children.OfType<TextBox>().Single();
                box.Text="안녕하세요 글자 수가 늘어나는 입력 테스트";Check(box.Width>initialWidth&&box.Width>=item.Width+item.FontSize-.01&&box.VerticalScrollBarVisibility==ScrollBarVisibility.Hidden,"Typing grows box immediately");
                box.Text="가";Check(box.Width<initialWidth&&item.X==60&&item.Y==80,"Deleting shrinks width without moving origin");
                box.Text="첫째 줄\n둘째 줄\n셋째 줄";Check(box.Height>initialHeight*1.8,"Newlines grow height");
                ((Action)Field(view,"addedTextCommit")!)();view.Undo();Check(((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Single(x=>x.Id==id).Text=="안녕하세요"&&Math.Abs(((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Single(x=>x.Id==id).Width-initialWidth)<.01,"Text and size Undo together");view.Redo();
                item=((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Single(x=>x.Id==id);Set(view,"selectedId",id);((HashSet<string>)Field(view,"selectedIds")!).Clear();Call(view,"DrawElements");
                var thumb=canvas.Children.OfType<Thumb>().Single(x=>!Equals(x.Tag,"object-rotation"));var font=item.FontSize;
                thumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});thumb.RaiseEvent(new DragDeltaEventArgs(70,0){RoutedEvent=Thumb.DragDeltaEvent});thumb.RaiseEvent(new DragCompletedEventArgs(70,0,false){RoutedEvent=Thumb.DragCompletedEvent});
                Check(!item.AutoWidth&&item.FontSize==font,"Manual text width does not enlarge font");var manualWidth=item.Width;
                Call(view,"BeginAddedText");await Task.Delay(100);box=canvas.Children.OfType<TextBox>().Single();box.Text="가";Check(Math.Abs(box.Width-manualWidth)<.01,"Manual width remains fixed while editing");((Action)Field(view,"addedTextCommit")!)();
                Call(view,"FitSelectedTextToContent");Check(item.AutoWidth&&item.Width<manualWidth,"Fit-to-content restores automatic width");
                var oldHeight=item.Height;Call(view,"ApplyContextProperty","font","36");Check(item.FontSize==36&&item.Height>oldHeight,"Font size reflows bounds");
                // Page-edge wrapping stays within remaining width; page origin is stable.
                view.AddText(470,300,"페이지 경계에서 자동 줄바꿈되는 긴 한글 문장입니다. 여러 글자를 입력합니다.");var edge=((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Last();
                Check(edge.X==470&&edge.Width<=130.1&&edge.Height>40,"Long text wraps at page boundary");
                view.AddText(60,450,"첫 메모",true);var note=((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Last();var noteWidth=note.Width;
                Call(view,"BeginAddedText");await Task.Delay(100);box=canvas.Children.OfType<TextBox>().Single();box.Text=string.Join("\n",Enumerable.Repeat("메모 줄 추가",5));Check(box.Width==noteWidth&&box.Height>=100,"Memo keeps minimum width and grows vertically");((Action)Field(view,"addedTextCommit")!)();Call(view,"FitSelectedTextToContent");Check(note.Width>=120,"Memo fit preserves minimum width");
                tool.SelectedIndex=5;Call(view,"BeginGraphic",new Point(60,620));Call(view,"BeginGraphic",new Point(250,620));Call(view,"EndGraphic");var line1=((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Last().Id;
                Call(view,"BeginGraphic",new Point(60,650));Call(view,"BeginGraphic",new Point(250,650));Call(view,"EndGraphic");var line2=((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Last().Id;tool.SelectedIndex=1;
                Set(view,"selectedId",line1);var selection=(HashSet<string>)Field(view,"selectedIds")!;selection.Clear();selection.UnionWith(new[]{line1,line2});
                Call(view,"PrepareAnnotationContext",new Point(120,620),true);Check(selection.Count==2,"Right click inside group keeps selection");
                var history=History(view);Call(view,"ApplyContextProperty","line","dashdotdot");Check(((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Where(x=>x.Id==line1||x.Id==line2).All(x=>x.LineStyle=="dashdotdot")&&History(view)==history+1,"Group style is one Undo unit");
                view.Undo();Check(((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Where(x=>x.Id==line1||x.Id==line2).All(x=>x.LineStyle!="dashdotdot"),"Group style Undo");view.Redo();
                Call(view,"ApplyContextProperty","color","#1755B5");Call(view,"ApplyContextProperty","width","4");
                var before=JsonSerializer.Serialize(((PdfEditPlan)Field(view,"plan")!));history=History(view);Call(view,"PrepareAnnotationContext",new Point(550,730),true);Call(view,"ApplyContextProperty","color","#E51B23");Call(view,"ApplyContextProperty","line","dotted");Call(view,"ApplyContextProperty","width","2");
                Check(before==JsonSerializer.Serialize(((PdfEditPlan)Field(view,"plan")!))&&History(view)==history&&((HashSet<string>)Field(view,"selectedIds")!).Count==0,"Blank-space defaults do not alter objects/history");
                tool.SelectedIndex=5;Call(view,"BeginGraphic",new Point(300,620));Call(view,"BeginGraphic",new Point(500,620));Call(view,"EndGraphic");var last=((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Last();Check(last.Color=="#E51B23"&&last.LineStyle=="dotted"&&last.StrokeWidth==2,"New line inherits context defaults");tool.SelectedIndex=1;
                Call(view,"PrepareAnnotationContext",new Point(120,620),true);Call(view,"BuildAnnotationContextMenu");var menu=canvas.ContextMenu;Check(menu.Items.OfType<MenuItem>().Any(x=>x.Header?.ToString()=="색상"),"Context property menus constructed");
                // Use real MenuItem OnClick for a thickness option, through the same path as a user.
                var widthMenu=menu.Items.OfType<MenuItem>().Single(x=>x.Header?.ToString()=="선 두께");var option=widthMenu.Items.OfType<MenuItem>().Single(x=>x.Header?.ToString()=="8 pt");typeof(MenuItem).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(option,null);await Task.Delay(150);Check(((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Single(x=>x.Id==line1).StrokeWidth==8,"Context option click applies style: "+JsonSerializer.Serialize(((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Select(x=>new{x.Id,x.Kind,x.X,x.Y,x.StrokeWidth}))+" selection="+Field(view,"selectedId"));
                Call(view,"BuildAnnotationContextMenu");var colorMenu=menu.Items.OfType<MenuItem>().Single(x=>x.Header?.ToString()=="색상");var palette=(UniformGrid)((MenuItem)colorMenu.Items[0]).Header;
                var colorButton=palette.Children.OfType<Button>().Single(x=>x.ToolTip?.ToString()=="#FF6500");typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(colorButton,null);Check(((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements.Single(x=>x.Id==line1).Color=="#FF6500","Palette button applies selected object color");
                var folder=Path.GetFullPath("artifacts/context-text-test");Directory.CreateDirectory(folder);var token=Guid.NewGuid().ToString("N");
                await view.SaveCopyAsync(Path.Combine(folder,token+".pdf"));var project=Path.Combine(folder,token+".topdf");await view.SaveProjectAsync(project);var json=JsonSerializer.Serialize(((PdfEditPlan)Field(view,"plan")!));await view.LoadProjectAsync(project);Check(JsonSerializer.Serialize(((PdfEditPlan)Field(view,"plan")!))==json,"Auto/manual sizes and context styles survive project round trip");
                Check(original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(input))),"Original unchanged");window.UpdateLayout();var bmp=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bmp.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using(var stream=File.Create(Path.Combine(folder,"editor.png")))encoder.Save(stream);
                Console.WriteLine("PASS context/text: live width/height, fixed origin, manual width/font, fit/font-size, edge wrapping, memo growth, group properties Undo, blank defaults, actual menu click, PDF/project round trip, original unchanged");result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}finally{Set(view,"addedTextCommit",null);Set(view,"dirty",false);window.Close();app.Shutdown();}
        };app.Run();return result;
    }
}

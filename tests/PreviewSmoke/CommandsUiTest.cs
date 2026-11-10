using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Topdf;
internal static class CommandsUiTest
{
    static object? Call(PdfEditorView view,string name,params object[] args)=>typeof(PdfEditorView).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,args);
    static object? Field(PdfEditorView view,string name)=>typeof(PdfEditorView).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view);
    static void Set(PdfEditorView view,string name,object? value)=>typeof(PdfEditorView).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,value);
    static int History(PdfEditorView view)=>((ICollection)Field(view,"undo")!).Count;
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    static string Plan(PdfEditorView view)=>JsonSerializer.Serialize(view.CurrentPlan);
    static async Task Until(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));while(!condition())await Task.Delay(100,timeout.Token);}
    public static int Run(string input)
    {
        int result=1;var app=new Application();app.Startup+=async(_,_)=>{
            var window=new MainWindow();window.Show();
            try{
                var source=Path.GetFullPath(input);var hash=SHA256.HashData(File.ReadAllBytes(source));window.Add([source]);await Until(()=>window.ActivePdfEditor!=null);var view=window.ActivePdfEditor!;await view.Ready;
                Check(!ApplicationCommands.Undo.CanExecute(null,view),"Native viewing does not consume object undo");
                var tool=view.RibbonButtons.Single(x=>x.Tag.ToString()=="6");tool.IsChecked=true;tool.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));await Until(()=>!view.IsViewing);
                var canvas=(Canvas)view.FindName("EditCanvas");var selector=(ComboBox)view.FindName("EditTool");var pageField=(TextBox)view.FindName("EditPageNumber");pageField.Focus();
                Check(ApplicationCommands.Undo.InputGestures.OfType<KeyGesture>().Any(x=>x.Key==Key.Z&&x.Modifiers==ModifierKeys.Control),"Windows Ctrl+Z gesture registered");
                Check(view.InputBindings.OfType<KeyBinding>().Any(x=>x.Key==Key.Z&&x.Modifiers==(ModifierKeys.Control|ModifierKeys.Shift)&&x.Command==ApplicationCommands.Redo),"Ctrl+Shift+Z redo alias");
                Call(view,"BeginGraphic",new Point(60,120));Check(ReferenceEquals(Keyboard.FocusedElement,canvas),"Drawing takes keyboard focus away from page field");
                Call(view,"UpdateGraphic",new Point(240,180));Call(view,"EndGraphic");var arrowId=view.CurrentPlan.Pages[0].Elements.Single().Id;
                Check(ApplicationCommands.Undo.CanExecute(null,canvas),"Undo available after draw");CommandManager.InvalidateRequerySuggested();await Dispatcher.Yield(DispatcherPriority.Background);Check(((Button)view.FindName("UndoButton")).IsEnabled,"Undo ribbon enabled after draw");typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke((Button)view.FindName("UndoButton"),null);Check(view.CurrentPlan.Pages[0].Elements.Count==0,"Routed Undo removes one stroke");
                CommandManager.InvalidateRequerySuggested();await Dispatcher.Yield(DispatcherPriority.Background);
                Check(!((Button)view.FindName("UndoButton")).IsEnabled&&((Button)view.FindName("RedoButton")).IsEnabled,"Ribbon reflects undo/redo availability");
                ApplicationCommands.Redo.Execute(null,canvas);Check(view.CurrentPlan.Pages[0].Elements.Count==1,"Routed Redo restores stroke");
                var history=History(view);((ComboBox)view.FindName("EditZoom")).Focus();Call(view,"BeginGraphic",new Point(70,240));Check(ReferenceEquals(Keyboard.FocusedElement,canvas),"Drawing takes focus from zoom control");Call(view,"UpdateGraphic",new Point(210,280));ApplicationCommands.Undo.Execute(null,canvas);
                Check(view.CurrentPlan.Pages[0].Elements.Count==1&&History(view)==history,"Undo during draw cancels pending stroke only");ApplicationCommands.Undo.Execute(null,canvas);Check(view.CurrentPlan.Pages[0].Elements.Count==0,"Earlier undo history remains intact");ApplicationCommands.Redo.Execute(null,canvas);
                selector.SelectedIndex=7;Call(view,"BeginGraphic",new Point(80,300));Call(view,"UpdateGraphic",new Point(200,370));Call(view,"EndGraphic");ApplicationCommands.Undo.Execute(null,canvas);
                selector.SelectedIndex=8;Call(view,"BeginGraphic",new Point(280,300));Call(view,"UpdateGraphic",new Point(380,370));Call(view,"EndGraphic");Check(!ApplicationCommands.Redo.CanExecute(null,canvas),"New edit clears redo branch");
                selector.SelectedIndex=1;Set(view,"selectedId",arrowId);((HashSet<string>)Field(view,"selectedIds")!).Clear();Call(view,"DrawElements");
                var thumb=canvas.Children.OfType<Thumb>().Last();var before=Plan(view);history=History(view);
                thumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});thumb.RaiseEvent(new DragDeltaEventArgs(20,25){RoutedEvent=Thumb.DragDeltaEvent});Check(Plan(view)!=before,"Endpoint gesture changes model temporarily");
                ApplicationCommands.Undo.Execute(null,canvas);Check(Plan(view)==before&&History(view)==history,"Undo cancels active endpoint gesture without consuming history");
                var shapeId=view.CurrentPlan.Pages[0].Elements.Single(x=>x.Kind=="ellipse").Id;Set(view,"selectedId",shapeId);((HashSet<string>)Field(view,"selectedIds")!).Clear();Call(view,"DrawElements");
                var resize=canvas.Children.OfType<Thumb>().Single();before=Plan(view);history=History(view);
                resize.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});resize.RaiseEvent(new DragDeltaEventArgs(30,0){RoutedEvent=Thumb.DragDeltaEvent});ApplicationCommands.Undo.Execute(null,canvas);
                resize.RaiseEvent(new DragCompletedEventArgs(30,0,false){RoutedEvent=Thumb.DragCompletedEvent});Check(Plan(view)==before&&History(view)==history,"Resize cancel and late mouse-up do not commit again");
                ApplicationCommands.SelectAll.Execute(null,canvas);before=Plan(view);history=History(view);
                var border=canvas.Children.OfType<Border>().First(x=>x.Tag is PdfEditElement);
                Call(view,"ElementMouseDown",border,new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.MouseDownEvent});
                foreach(var moving in ((PdfEditPlan)Field(view,"plan")!).Pages[0].Elements){moving.X+=5;moving.Y+=7;}
                ApplicationCommands.Undo.Execute(null,canvas);Call(view,"PageMouseUp",canvas,new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left));Check(Plan(view)==before&&History(view)==history,"Group move cancel preserves history");
                ApplicationCommands.SelectAll.Execute(null,canvas);ApplicationCommands.Copy.Execute(null,canvas);Check(ApplicationCommands.Paste.CanExecute(null,canvas),"Object copy enables paste");
                var count=view.CurrentPlan.Pages[0].Elements.Count;ApplicationCommands.Cut.Execute(null,canvas);Check(view.CurrentPlan.Pages[0].Elements.Count==0,"Cut removes selected group");ApplicationCommands.Undo.Execute(null,canvas);Check(view.CurrentPlan.Pages[0].Elements.Count==count,"Cut is a single undo unit");
                ApplicationCommands.Paste.Execute(null,canvas);Check(view.CurrentPlan.Pages[0].Elements.Count==count*2,"Paste duplicates group");ApplicationCommands.Delete.Execute(null,canvas);Check(view.CurrentPlan.Pages[0].Elements.Count==count,"Delete removes pasted selection");ApplicationCommands.Undo.Execute(null,canvas);ApplicationCommands.Redo.Execute(null,canvas);
                view.AddText(280,450,"원래 메모",true);Call(view,"BeginAddedText");await Task.Delay(150);var box=canvas.Children.OfType<TextBox>().Single();Check(!box.CanUndo,"Initial memo content is not an undo operation");box.CaretIndex=box.Text.Length;box.AppendText(" 추가 입력");
                Check(box.IsKeyboardFocused&&box.CanUndo,"Memo has local text undo history");Check(ReferenceEquals(view.InputBindings.OfType<KeyBinding>().Single(x=>x.Command==ApplicationCommands.Redo).CommandTarget,box),"Redo alias targets active memo input");before=Plan(view);history=History(view);typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke((Button)view.FindName("UndoButton"),null);
                Check(box.Text=="원래 메모"&&Plan(view)==before&&History(view)==history,"Text Undo never deletes annotations");typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke((Button)view.FindName("RedoButton"),null);Check(box.Text.Contains("추가 입력"),"Ribbon Text Redo remains native");
                box.IsUndoEnabled=false;box.IsUndoEnabled=true;Check(!ApplicationCommands.Undo.CanExecute(null,box),"Empty text history does not fall through to object undo");
                ((Action)Field(view,"addedTextCommit")!)();Keyboard.Focus(canvas);ApplicationCommands.Undo.Execute(null,canvas);Check(view.CurrentPlan.Pages[0].Elements.Single(x=>x.Kind=="note").Text=="원래 메모","Committed memo edit is one object history unit");ApplicationCommands.Redo.Execute(null,canvas);
                var context=canvas.ContextMenu;context.PlacementTarget=canvas;context.IsOpen=true;await Dispatcher.Yield(DispatcherPriority.Loaded);
                Check(context.Items.OfType<MenuItem>().All(x=>ReferenceEquals(x.CommandTarget,canvas)),"Context menu targets same canvas commands");context.IsOpen=false;
                var folder=Path.GetFullPath("artifacts/commands-test");Directory.CreateDirectory(folder);await view.SaveProjectAsync(Path.Combine(folder,Guid.NewGuid()+".topdf"));Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))),"Source preserved");
                Console.WriteLine("PASS Windows commands: Ctrl+Z gesture/focus, routed undo/redo/buttons/context, partial stroke/endpoint cancellation, redo branch, group cut/copy/paste/delete, native memo text undo, source/project preservation");result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}finally{if(window.ActivePdfEditor is {} view){Set(view,"addedTextCommit",null);Set(view,"inlineText",null);Set(view,"selectedSource",null);Set(view,"dirty",false);}window.Close();app.Shutdown();}
        };app.Run();return result;
    }
}

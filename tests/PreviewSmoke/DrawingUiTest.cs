using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Topdf;
internal static class DrawingUiTest
{
    static object? Call(PdfEditorView view,string name,params object[] args)=>typeof(PdfEditorView).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,args);
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static int Run(string input)
    {
        var result=1;var app=new Application();app.Startup+=async(_,_)=>{
            var window=new PdfEditorWindow(Path.GetFullPath(input));window.Show();
            try{
                await window.Ready.WaitAsync(TimeSpan.FromSeconds(90));var view=window.View;
                var original=SHA256.HashData(File.ReadAllBytes(input));
                var tool=(ComboBox)view.FindName("EditTool");
                var ribbon=(StackPanel)view.FindName("EditorRibbon");Check(view.RibbonButtons.Count(x=>x.Tag.ToString()!="-1")==13,"Thirteen ribbon icons");
                var names=new[]{"선","화살표","사각형","타원","형광펜","밑줄","취소선","자유곡선"};
                for(int n=0;n<names.Length;n++){
                    tool.SelectedItem=tool.Items.Cast<ComboBoxItem>().Single(x=>(string)x.Content==names[n]);
                    Call(view,"BeginGraphic",new Point(50,80+n*65));
                    Call(view,"UpdateGraphic",new Point(130,90+n*65));Call(view,"UpdateGraphic",new Point(240,115+n*65));
                    var drawingCanvas=(Canvas)view.FindName("EditCanvas");Check(drawingCanvas.Children.OfType<Border>().Where(x=>x.Tag is PdfEditElement).All(x=>x.BorderThickness.Left==0)&&!drawingCanvas.Children.OfType<Thumb>().Any(),"No blue selection box or handles during drawing");
                    Call(view,"EndGraphic");Check(!drawingCanvas.Children.OfType<Thumb>().Any(),"Drawing tool keeps finished annotations undecorated");
                }
                Check(view.CurrentPlan.Pages[0].Elements.Count==8,"All drawing tools");
                Check(view.RibbonButtons.Single(x=>x.IsChecked==true).Tag.ToString()=="12","Ribbon selection follows tool");
                Check(view.CurrentPlan.Pages[0].Elements.All(x=>x.Color=="#FF6500"),"Orange default for new annotations");
                tool.SelectedIndex=1;
                var selectionCanvas=(Canvas)view.FindName("EditCanvas");Check(selectionCanvas.Children.OfType<Thumb>().Any(),"Selection tool restores handles");
                tool.SelectedIndex=6;Check(!selectionCanvas.Children.OfType<Thumb>().Any()&&selectionCanvas.Children.OfType<Border>().Where(x=>x.Tag is PdfEditElement).All(x=>x.BorderThickness.Left==0),"Switching to draw hides old selection decorations immediately");tool.SelectedIndex=1;
                var hitPlan=(PdfEditPlan)typeof(PdfEditorView).GetField("plan",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
                var hollow=new PdfEditElement {Kind="rectangle",X=300,Y=650,Width=200,Height=150,StrokeWidth=2};
                var diagonal=new PdfEditElement {Kind="arrow",X=320,Y=670,Width=160,Height=110,StrokeWidth=2,Points=[new[]{0.0,1.0},new[]{1.0,0.0}]};
                var filled=new PdfEditElement {Kind="ellipse",X=370,Y=695,Width=60,Height=60,Fill="#FFFFFF",StrokeWidth=2};
                hitPlan.Pages[0].Elements.AddRange([diagonal,hollow]);
                var crossing=new Point(400,725);
                Check(!(bool)Call(view,"AnnotationHit",hollow,crossing)!&&(bool)Call(view,"AnnotationHit",hollow,new Point(301,725))!,"Hollow shape interior passes through, edge remains selectable");
                Check(((PdfEditElement)Call(view,"AnnotationAt",crossing,false)!).Id==diagonal.Id,"Arrow selected through hollow rectangle");
                Check(!(bool)Call(view,"AnnotationHit",diagonal,new Point(330,675))!,"Empty arrow bounding area does not intercept clicks");
                hitPlan.Pages[0].Elements.Add(filled);typeof(PdfEditorView).GetField("selectedId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,filled.Id);
                Check(((PdfEditElement)Call(view,"AnnotationAt",crossing,false)!).Id==filled.Id&&((PdfEditElement)Call(view,"AnnotationAt",crossing,true)!).Id==diagonal.Id,"Filled shape hit and Alt cycling reach underlying arrow");
                Call(view,"DrawElements");
                var hitEvent=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseDownEvent};
                Call(view,"SelectHitAnnotation",crossing,false,hitEvent);Call(view,"PageMouseUp",selectionCanvas,new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left));
                Check(hitEvent.Handled&&((string)typeof(PdfEditorView).GetField("selectedId",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!)==filled.Id,"Pointer hit routes into object selection");
                Call(view,"SelectHitAnnotation",crossing,true,hitEvent);Call(view,"PageMouseUp",selectionCanvas,new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left));
                Check(((string)typeof(PdfEditorView).GetField("selectedId",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!)==diagonal.Id&&selectionCanvas.Children.OfType<Thumb>().Count()==2,"Cycling selects underlying arrow with editable endpoints");
                diagonal.Rotation=90;Check((bool)Call(view,"AnnotationHit",diagonal,crossing)!,"Rotated geometry hit test");
                hitPlan.Pages[0].Elements.RemoveAll(x=>x.Id==hollow.Id||x.Id==diagonal.Id||x.Id==filled.Id);typeof(PdfEditorView).GetField("selectedId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,view.CurrentPlan.Pages[0].Elements.Last().Id);Call(view,"DrawElements");
                Call(view,"BeginAreaSelection",new Point(35,65),System.Windows.Input.ModifierKeys.None);Call(view,"UpdateAreaSelection",new Point(265,200));Call(view,"FinishAreaSelection",false);
                var areaSelection=(HashSet<string>)typeof(PdfEditorView).GetField("selectedIds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
                Check(areaSelection.Count==2,"Drag area selects multiple annotations: "+areaSelection.Count+" of "+view.CurrentPlan.Pages[0].Elements.Count+" "+string.Join(",",view.CurrentPlan.Pages[0].Elements.Select(x=>$"{x.Kind}:{x.X:0.0},{x.Y:0.0}")));
                var selectedBefore=view.CurrentPlan.Pages[0].Elements.Where(x=>areaSelection.Contains(x.Id)).ToArray();
                Call(view,"NudgeSelection",4.0,6.0);
                Check(view.CurrentPlan.Pages[0].Elements.Where(x=>areaSelection.Contains(x.Id)).All(x=>Math.Abs(x.X-selectedBefore.Single(y=>y.Id==x.Id).X-4)<.01),"Selected group moves together");view.Undo();
                Call(view,"BeginAreaSelection",new Point(0,0),System.Windows.Input.ModifierKeys.Shift);Call(view,"UpdateAreaSelection",new Point(300,650));Call(view,"FinishAreaSelection",false);Check(areaSelection.Count==8,"Shift adds area to selection");
                Call(view,"BeginAreaSelection",new Point(0,0),System.Windows.Input.ModifierKeys.Control);Call(view,"UpdateAreaSelection",new Point(300,650));Call(view,"FinishAreaSelection",false);Check(areaSelection.Count==0,"Ctrl toggles selected area");
                Call(view,"BeginAreaSelection",new Point(0,0),System.Windows.Input.ModifierKeys.None);Call(view,"UpdateAreaSelection",new Point(300,650));Call(view,"FinishAreaSelection",true);Check(areaSelection.Count==0,"Canceled marquee restores previous selection");
                tool.SelectedIndex=6;
                foreach(var offset in new[]{new Point(0,0),new Point(.4,.3),new Point(1,1)}){
                    Call(view,"BeginGraphic",new Point(60,60));Call(view,"UpdateGraphic",new Point(60+offset.X,60+offset.Y));Call(view,"EndGraphic");
                    Check(view.CurrentPlan.Pages[0].Elements.Count==8,"Click and mouse jitter do not create arrowheads");
                }
                tool.SelectedIndex=12;
                Call(view,"BeginGraphic",new Point(60,60));Call(view,"UpdateGraphic",new Point(180,150));
                var escape=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,System.Windows.Input.Key.Escape){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
                Call(view,"EditorShortcuts",view,escape);
                Check(escape.Handled&&tool.SelectedIndex==1&&view.CurrentPlan.Pages[0].Elements.Count==8,"Esc cancels unfinished stroke and selects pointer, preserving completed drawings");
                tool.SelectedIndex=6;Call(view,"EditorShortcuts",view,escape);Check(tool.SelectedIndex==1&&view.CurrentPlan.Pages[0].Elements.Count==8,"Esc leaves drawing tool without deleting completed annotations");
                var endpointCanvas=(Canvas)view.FindName("EditCanvas");
                var arrow=view.CurrentPlan.Pages[0].Elements.Single(x=>x.Kind=="arrow");
                typeof(PdfEditorView).GetField("selectedId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,arrow.Id);Call(view,"DrawElements");
                Call(view,"RotateObjects",view,new RoutedEventArgs());
                var model=(PdfEditPlan)typeof(PdfEditorView).GetField("plan",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
                var activeArrow=model.Pages[0].Elements.Single(x=>x.Id==arrow.Id);var originalEnds=(Point[])Call(view,"GraphicEndpoints",activeArrow)!;
                var ends=endpointCanvas.Children.OfType<Thumb>().Where(x=>(x.Tag as string)=="endpoint").ToArray();Check(ends.Length==2,"Arrow has two endpoint handles");
                ends[1].RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
                ends[1].RaiseEvent(new DragDeltaEventArgs(-35,25){RoutedEvent=Thumb.DragDeltaEvent});
                var editedEnds=(Point[])Call(view,"GraphicEndpoints",activeArrow)!;
                Check((editedEnds[0]-originalEnds[0]).Length<.01&&(editedEnds[1]-(originalEnds[1]+new Vector(-35,25))).Length<.01,"Rotated arrow endpoint follows drag; opposite end fixed");
                ends[1].RaiseEvent(new DragCompletedEventArgs(-35,25,false){RoutedEvent=Thumb.DragCompletedEvent});
                view.Undo();view.Redo();
                typeof(PdfEditorView).GetField("selectedId",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,view.CurrentPlan.Pages[0].Elements.Last().Id);Call(view,"DrawElements");
                var countBeforeDelete=view.CurrentPlan.Pages[0].Elements.Count;
                Call(view,"DeleteElement",view,new RoutedEventArgs());Check(view.CurrentPlan.Pages[0].Elements.Count==countBeforeDelete-1,"Selected graphic deletes");
                view.Undo();Check(view.CurrentPlan.Pages[0].Elements.Count==countBeforeDelete,"Delete undo restores graphic");
                var palette=(System.Windows.Controls.Primitives.UniformGrid)view.FindName("PenPalette");Check(palette.Children.Count==30,"Palette colors");
                palette.Children.OfType<RadioButton>().Single(x=>(string)x.Tag=="#1755B5").IsChecked=true;((Slider)view.FindName("PenWidthSlider")).Value=3.5;
                Check(view.CurrentPlan.Pages[0].Elements.Last().Color=="#1755B5" && view.CurrentPlan.Pages[0].Elements.Last().StrokeWidth==3.5,"Palette style applied");
                ((System.Windows.Controls.Primitives.ToggleButton)view.FindName("PenOptions")).IsChecked=true;await Task.Delay(100);
                var popup=(System.Windows.Controls.Primitives.Popup)view.FindName("PenPopup");var popupBody=(FrameworkElement)popup.Child;popupBody.UpdateLayout();
                var paletteBitmap=new RenderTargetBitmap((int)popupBody.ActualWidth,(int)popupBody.ActualHeight,96,96,PixelFormats.Pbgra32);paletteBitmap.Render(popupBody);
                var paletteEncoder=new PngBitmapEncoder();paletteEncoder.Frames.Add(BitmapFrame.Create(paletteBitmap));Directory.CreateDirectory("artifacts/drawing-test");using(var file=File.Create("artifacts/drawing-test/palette.png"))paletteEncoder.Save(file);
                ((System.Windows.Controls.Primitives.ToggleButton)view.FindName("PenOptions")).IsChecked=false;
                Check(view.CurrentPlan.Pages[0].Elements.All(x=>x.Points.All(p=>p.All(v=>v>=0&&v<=1))),"Normalized points");
                tool.SelectedIndex=3;
                var noteCanvas=(Canvas)view.FindName("EditCanvas");
                var press=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.MouseDownEvent};
                Call(view,"PageMouseDown",noteCanvas,press);Check(press.Handled&&tool.SelectedIndex==1,"Memo click handled and returns to selection");
                Call(view,"PageMouseUp",noteCanvas,new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left));
                await Task.Delay(200);
                Check(noteCanvas.Children.OfType<TextBox>().Single().IsKeyboardFocusWithin,"Memo input survives mouse-up and keeps keyboard focus");
                var memo=((Canvas)view.FindName("EditCanvas")).Children.OfType<TextBox>().Single();memo.Text="검토 메모\n치수와 출입구 확인";
                ((Action)typeof(PdfEditorView).GetField("addedTextCommit",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!)();
                Check(view.CurrentPlan.Pages[0].Elements.Single(x=>x.Kind=="note").Text.Contains("치수와 출입구"),"Direct Korean multiline memo retained");
                view.Undo();view.Redo();Check(view.CurrentPlan.Pages[0].Elements.Single(x=>x.Kind=="note").Text.Contains("검토 메모"),"Memo undo redo");
                view.AddText(280,220,"직접 수정 · 가운데 정렬");
                ((ComboBox)view.FindName("TextFont")).SelectedIndex=2;((ComboBox)view.FindName("TextAlign")).SelectedIndex=1;
                Call(view,"ApplyStyle",view,new RoutedEventArgs());
                var selected=(HashSet<string>)typeof(PdfEditorView).GetField("selectedIds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
                selected.UnionWith(view.CurrentPlan.Pages[0].Elements.Where(x=>x.Kind=="rectangle"||x.Kind=="ellipse").Select(x=>x.Id));
                view.CopyObjects();view.PasteObjects();Check(view.CurrentPlan.Pages[0].Elements.Count==13,"Group copy paste");
                Call(view,"RotateObjects",view,new RoutedEventArgs());
                view.Undo();view.Redo();view.Undo();
                var canvas=(Canvas)view.FindName("EditCanvas");var thumb=canvas.Children.OfType<Thumb>().Single();
                thumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
                thumb.RaiseEvent(new DragDeltaEventArgs(20,20){RoutedEvent=Thumb.DragDeltaEvent});
                thumb.RaiseEvent(new DragCompletedEventArgs(20,20,false){RoutedEvent=Thumb.DragCompletedEvent});
                view.Undo();
                Call(view,"BringForward",view,new RoutedEventArgs());Call(view,"SendBackward",view,new RoutedEventArgs());
                ((ComboBox)view.FindName("EditZoom")).SelectedIndex=3;view.RotateSelectedPage(90);view.Undo();
                var folder=Path.GetFullPath("artifacts/drawing-test");Directory.CreateDirectory(folder);var id=Guid.NewGuid().ToString("N");
                var project=Path.Combine(folder,id+".topdf");await view.SaveProjectAsync(project);
                var malicious=Path.Combine(folder,id+"-invalid.topdf");using(var zip=System.IO.Compression.ZipFile.Open(malicious,System.IO.Compression.ZipArchiveMode.Create)){var entry=zip.CreateEntry("../escape.txt");using(var stream=new StreamWriter(entry.Open()))stream.Write("invalid");zip.CreateEntry("plan.json");}
                var refused=false;try{await view.LoadProjectAsync(malicious);}catch(IOException){refused=true;}Check(refused&&!File.Exists(Path.Combine(folder,"escape.txt")),"Project traversal refused");
                var json=System.Text.Json.JsonSerializer.Serialize(view.CurrentPlan);await view.LoadProjectAsync(project);
                Check(json==System.Text.Json.JsonSerializer.Serialize(view.CurrentPlan),"Editable project round trip");
                window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(folder,"editor.png")))encoder.Save(stream);
                var output=Path.Combine(folder,id+".pdf");await view.SaveCopyAsync(output);Check(original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(input))),"Original preserved");
                // Constructor entry path allows opening a project from the main file menu.
                var second=new PdfEditorWindow(project);second.Show();await second.Ready.WaitAsync(TimeSpan.FromSeconds(90));Check(second.CurrentPlan.Pages[0].Elements.Count==13,"Project direct open");second.Close();
                var main=new MainWindow();main.Show();await main.OpenFilesAsync([project]);Check(main.ActivePdfEditor?.CurrentPlan.Pages[0].Elements.Count==13,"Main file menu project route");main.Close();
                Console.WriteLine("PASS drawings: eight tools, normalized gestures, font/alignment, group copy/paste, rotation, resize handle, z-order, undo/redo, zoom, editable project round trip/direct open, PDF export, original preserved. "+output);result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}
            finally{typeof(PdfEditorView).GetField("addedTextCommit",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window.View,null);typeof(PdfEditorView).GetField("dirty",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window.View,false);window.Close();app.Shutdown();}
        };app.Run();return result;
    }
}

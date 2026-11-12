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
                    if(names[n] is "선" or "화살표")Call(view,"BeginGraphic",new Point(240,115+n*65));
                    else {Call(view,"UpdateGraphic",new Point(130,90+n*65));Call(view,"UpdateGraphic",new Point(240,115+n*65));}
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
                Call(view,"SelectHitAnnotation",crossing,false,hitEvent);Call(view,"PageMouseUp",selectionCanvas,new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseDownEvent});
                Check(hitEvent.Handled&&((string)typeof(PdfEditorView).GetField("selectedId",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!)==filled.Id,"Pointer hit routes into object selection");
                Call(view,"SelectHitAnnotation",crossing,true,hitEvent);Call(view,"PageMouseUp",selectionCanvas,new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseDownEvent});
                Check(((string)typeof(PdfEditorView).GetField("selectedId",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!)==diagonal.Id&&selectionCanvas.Children.OfType<Thumb>().Count(x=>Equals(x.Tag,"endpoint"))==2,"Cycling selects underlying arrow with editable endpoints");
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
                    Call(view,"BeginGraphic",new Point(60,60));Call(view,"BeginGraphic",new Point(60+offset.X,60+offset.Y));Call(view,"EndGraphic");
                    Check(view.CurrentPlan.Pages[0].Elements.Count==8,"Click and mouse jitter do not create arrowheads");
                }
                var first=new Point(60,60);var secondPoint=new Point(180,60);var third=new Point(180,150);
                Call(view,"BeginGraphic",first);
                Call(view,"PreviewContinuousLine",secondPoint);
                var head=(System.Windows.Shapes.Polyline)typeof(PdfEditorView)
                    .GetField("continuousArrowHead",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
                Check(head.Points[1]==first&&view.CurrentPlan.Pages[0].Elements.Count==8,"Arrow preview points to the first click without saving");
                Call(view,"BeginGraphic",secondPoint);
                Call(view,"PreviewContinuousLine",third);
                Check(!selectionCanvas.Children.Contains(head),"Continuation preview has no arrowhead");
                Call(view,"BeginGraphic",third);
                Call(view,"BeginGraphic",new Point(240,150));
                var chain=view.CurrentPlan.Pages[0].Elements.TakeLast(3).ToArray();
                var firstEnds=(Point[])Call(view,"GraphicEndpoints",chain[0])!;
                var secondEnds=(Point[])Call(view,"GraphicEndpoints",chain[1])!;
                var thirdEnds=(Point[])Call(view,"GraphicEndpoints",chain[2])!;
                Check(chain[0].Kind=="arrow"&&chain.Skip(1).All(x=>x.Kind=="line")&&
                    (firstEnds[1]-first).Length<.001&&(firstEnds[0]-secondEnds[0]).Length<.001&&
                    (secondEnds[1]-third).Length<.001&&(secondEnds[1]-thirdEnds[0]).Length<.001,
                    "Only the first segment has an arrowhead and all vertices connect");
                Call(view,"EndGraphic");
                view.Undo();Check(view.CurrentPlan.Pages[0].Elements.Count==10,"Undo removes only the last segment");
                view.Redo();Check(view.CurrentPlan.Pages[0].Elements.Count==11,"Redo restores the last segment");
                view.Undo();view.Undo();view.Undo();
                Call(view,"BeginGraphic",first);Call(view,"BeginGraphic",secondPoint);Call(view,"EndGraphic");
                Check(view.CurrentPlan.Pages[0].Elements.Last().Kind=="arrow","A new chain starts with an arrowhead");
                view.Undo();
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
                Call(view,"PageMouseUp",noteCanvas,new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseDownEvent});
                await Task.Delay(200);
                Check(noteCanvas.Children.OfType<TextBox>().Single().IsKeyboardFocusWithin,"Memo input survives mouse-up and keeps keyboard focus");
                var memo=((Canvas)view.FindName("EditCanvas")).Children.OfType<TextBox>().Single();memo.Text="검토 메모\n치수와 출입구 확인";
                ((Action)typeof(PdfEditorView).GetField("addedTextCommit",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!)();
                Check(view.CurrentPlan.Pages[0].Elements.Single(x=>x.Kind=="note").Text.Contains("치수와 출입구"),"Direct Korean multiline memo retained");
                view.Undo();view.Redo();Check(view.CurrentPlan.Pages[0].Elements.Single(x=>x.Kind=="note").Text.Contains("검토 메모"),"Memo undo redo");
                var apply=(Button)view.FindName("ApplyObjectProperties");
                var rotate=(Button)view.FindName("RotateObjectButton");
                var front=(Button)view.FindName("BringObjectButton");
                var back=(Button)view.FindName("SendObjectButton");
                void Click(Button button) {
                    Check(button.IsEnabled,"Selected object enables "+button.Content);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                var selected=(HashSet<string>)typeof(PdfEditorView).GetField("selectedIds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
                var selectedId=typeof(PdfEditorView).GetField("selectedId",BindingFlags.Instance|BindingFlags.NonPublic)!;
                selected.Clear();selectedId.SetValue(view,"");Call(view,"DrawElements");
                Check(!apply.IsEnabled&&!rotate.IsEnabled&&!front.IsEnabled&&!back.IsEnabled,"Object commands disabled without a selection");
                var rectangle=view.CurrentPlan.Pages[0].Elements.Single(x=>x.Kind=="rectangle");
                selectedId.SetValue(view,rectangle.Id);Call(view,"DrawElements");
                ((CheckBox)view.FindName("FillShape")).IsChecked=true;
                Click(apply);
                Check(view.CurrentPlan.Pages[0].Elements.Single(x=>x.Id==rectangle.Id).Fill==rectangle.Color,"Fill applies through ribbon");
                ((CheckBox)view.FindName("FillShape")).IsChecked=false;

                view.AddText(280,220,"직접 수정 · 가운데 정렬");
                var text=view.CurrentPlan.Pages[0].Elements.Last();
                var originalSize=text.FontSize;
                var originalColor=text.Color;
                ((ComboBox)view.FindName("TextFont")).SelectedIndex=2;
                Check(view.CurrentPlan.Pages[0].Elements.Single(x=>x.Id==text.Id).FontFamily=="Batang","Font applies immediately");
                ((ComboBox)view.FindName("TextAlign")).SelectedIndex=1;
                var aligned=view.CurrentPlan.Pages[0].Elements.Single(x=>x.Id==text.Id);
                Check(aligned.Alignment=="center"&&aligned.FontSize==originalSize&&aligned.Color==originalColor,
                    "Alignment applies immediately without changing size or color");
                ((ComboBox)view.FindName("EditFontSize")).SelectedIndex=4;
                Check(view.CurrentPlan.Pages[0].Elements.Single(x=>x.Id==text.Id).FontSize==36,"Font size applies immediately");
                view.Undo();
                Check(view.CurrentPlan.Pages[0].Elements.Single(x=>x.Id==text.Id).FontSize==originalSize,
                    "Undo restores only the last formatting change");
                Check(((Canvas)view.FindName("EditCanvas")).Children.OfType<Thumb>().Any(x=>Equals(x.Tag,"object-rotation")),"Selection has a draggable rotation handle");
                Click(rotate);
                Check(view.CurrentPlan.Pages[0].Elements.Single(x=>x.Id==text.Id).Rotation==90,"Rotation applies through ribbon");
                view.Undo();
                Click(back);
                Check(view.CurrentPlan.Pages[0].Elements.First().Id==text.Id,"Send back applies through ribbon");
                Click(front);
                Check(view.CurrentPlan.Pages[0].Elements.Last().Id==text.Id,"Bring front applies through ribbon");
                selected.UnionWith(view.CurrentPlan.Pages[0].Elements.Where(x=>x.Kind=="rectangle"||x.Kind=="ellipse").Select(x=>x.Id));
                view.CopyObjects();view.PasteObjects();Check(view.CurrentPlan.Pages[0].Elements.Count==13,"Group copy paste");
                Call(view,"RotateObjects",view,new RoutedEventArgs());
                view.Undo();view.Redo();view.Undo();
                var canvas=(Canvas)view.FindName("EditCanvas");var thumb=canvas.Children.OfType<Thumb>().Single(x=>!Equals(x.Tag,"object-rotation"));
                thumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
                thumb.RaiseEvent(new DragDeltaEventArgs(20,20){RoutedEvent=Thumb.DragDeltaEvent});
                foreach(var visual in canvas.Children.OfType<Border>().Where(x=>x.Tag is PdfEditElement item&&selected.Contains(item.Id)&&x.Child is Canvas))
                {
                    var item=(PdfEditElement)visual.Tag;
                    var strokeCanvas=(Canvas)visual.Child;
                    Check(strokeCanvas.Width==item.Width&&strokeCanvas.Height==item.Height&&strokeCanvas.RenderTransform.Value.IsIdentity,
                        "Live resize uses final stroke geometry without scaling the old canvas");
                }
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
                var stampFile=Path.Combine(folder,id+"-stamp.png");
                var stampEncoder=new PngBitmapEncoder();
                var stampPixels=Enumerable.Repeat((byte)255,9*9*4).ToArray();
                for(var y=2;y<7;y++)for(var x=2;x<7;x++){
                    if(x==4&&y==4)continue;
                    var index=(y*9+x)*4;
                    stampPixels[index]=stampPixels[index+1]=0;
                }
                stampPixels[(1*9+1)*4+3]=0;
                var opaqueStamp=BitmapSource.Create(9,9,96,96,PixelFormats.Bgra32,null,stampPixels,36);
                var import=typeof(PdfEditorView).Assembly.GetType("Topdf.StampImportWindow")!;
                var cutout=(BitmapSource)import.GetMethod("RemoveBackgroundColor",BindingFlags.Static|BindingFlags.NonPublic)!
                    .Invoke(null,[opaqueStamp,Colors.White,30,false])!;
                var cutPixels=new byte[stampPixels.Length];
                cutout.CopyPixels(cutPixels,36,0);
                Check(cutPixels[3]==0&&cutPixels[(1*9+1)*4+3]==0&&cutPixels[(2*9+2)*4+3]==255&&cutPixels[(4*9+4)*4+3]==255,
                    "Stamp import clears only connected background and keeps ink, existing alpha and enclosed white");
                stampEncoder.Frames.Add(BitmapFrame.Create(cutout));
                using(var stream=File.Create(stampFile))stampEncoder.Save(stream);
                typeof(PdfEditorView).GetField("stamp",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,stampFile);
                var stampTool=(ComboBox)view.FindName("EditTool");
                stampTool.SelectedIndex=4;
                var stampPosition=new Point(45,160);
                var originalCount=view.CurrentPlan.Pages[0].Elements.Count;
                Call(view,"ShowStampPreview",stampPosition);
                var preview=canvas.Children.OfType<Image>().Single();
                Check(preview.Source!=null&&!preview.IsHitTestVisible&&view.CurrentPlan.Pages[0].Elements.Count==originalCount,
                    "Stamp image follows the pointer without placing an object");
                Call(view,"HideStampPreview");
                Check(!canvas.Children.Contains(preview),"Leaving the page removes the preview");
                Call(view,"ShowStampPreview",stampPosition);
                preview=canvas.Children.OfType<Image>().Single();
                var previewBounds=new Rect(Canvas.GetLeft(preview),Canvas.GetTop(preview),preview.Width,preview.Height);
                Check(previewBounds.BottomLeft==stampPosition,"Stamp preview is anchored at its bottom-left corner");
                Call(view,"PlaceStamp",stampPosition);
                Check(!canvas.Children.Contains(preview),"Click clears preview");
                var stamp=view.CurrentPlan.Pages[0].Elements.Last();
                Check(previewBounds==new Rect(stamp.X,stamp.Y,stamp.Width,stamp.Height),"Stamp is placed at the preview bounds");
                Check(stamp.Kind=="stamp"&&stampTool.SelectedIndex==1,"Placement automatically switches to selection");
                var hit=new Point(stamp.X+stamp.Width/2,stamp.Y+stamp.Height/2);
                Call(view,"SelectHitAnnotation",hit,false,new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseDownEvent});
                Point ChildOrigin()
                {
                    window.UpdateLayout();
                    var visual=canvas.Children.OfType<Border>().Single(x=>x.Tag is PdfEditElement item&&item.Id==stamp.Id);
                    return visual.Child.TranslatePoint(new Point(),visual);
                }
                selected.Clear();selectedId.SetValue(view,"");Call(view,"DrawElements");
                var unselectedOrigin=ChildOrigin();
                selectedId.SetValue(view,stamp.Id);Call(view,"DrawElements");
                Check(ChildOrigin()==unselectedOrigin,"Selection outline leaves content layout unchanged");
                var start=(Point)typeof(PdfEditorView).GetField("dragStart",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
                Call(view,"MoveDraggedObjects",start+new Vector(.25,.25));
                var clicked=view.CurrentPlan.Pages[0].Elements.Single(x=>x.Id==stamp.Id);
                Check(clicked.X==stamp.X&&clicked.Y==stamp.Y,"Pointer jitter during selection does not move the object");
                Call(view,"MoveDraggedObjects",start+new Vector(35,22));
                Call(view,"FinishObjectDrag");
                PdfEditElement Stamp()=>view.CurrentPlan.Pages[0].Elements.Single(item=>item.Id==stamp.Id);
                Check(Math.Abs(Stamp().X-stamp.X-35)<.01&&Math.Abs(Stamp().Y-stamp.Y-22)<.01,"Inserted stamp moves on both axes");
                view.Undo();
                Check(Math.Abs(Stamp().X-stamp.X)<.01,"Undo restores stamp position");
                view.Redo();
                var properties=(DockPanel)view.FindName("ObjectPropertiesPane");
                var fields=(StackPanel)view.FindName("ObjectPropertiesFields");
                var editScroll=(ScrollViewer)view.FindName("EditScroll");
                Check(properties.IsVisible,"Selecting an object opens properties");
                window.UpdateLayout();
                Check(properties.TranslatePoint(new Point(),window).X>=editScroll.TranslatePoint(new Point(),window).X+editScroll.ActualWidth-.5,
                    "Properties are docked beside the document");
                Control? Property(string label)=>fields.Children.OfType<StackPanel>()
                    .SelectMany(row=>row.Children.Cast<FrameworkElement>().SelectMany(child=>child is DockPanel dock?dock.Children.OfType<Control>():new[]{child}.OfType<Control>()))
                    .SingleOrDefault(control=>System.Windows.Automation.AutomationProperties.GetName(control)==label);
                var stampOpacity=(TextBox)Property("그림 투명도 (%)")!;
                Check(stampOpacity.Text=="0","Stamp transparency defaults to 0%");
                stampOpacity.Text="50";
                Check(canvas.Children.OfType<Border>().Single(x=>x.Tag is PdfEditElement item&&item.Id==stamp.Id).Child is Image {Opacity:.5},"Numeric input previews transparency before Enter");
                stampOpacity.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(stampOpacity),Environment.TickCount,System.Windows.Input.Key.Enter){
                    RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent
                });
                Check(Stamp().ImageOpacity==.5,"Docked transparency applies immediately");
                var stampVisual=canvas.Children.OfType<Border>().Single(x=>x.Tag is PdfEditElement item&&item.Id==stamp.Id);
                Check(stampVisual.Child is Image {Opacity:.5},"Stamp preview uses object transparency");
                view.Undo();Check(Stamp().ImageOpacity==1,"Docked transparency is undoable");
                var opacitySlider=(Slider)Property("그림 투명도 (%) 슬라이더")!;
                opacitySlider.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
                opacitySlider.Value=20;
                opacitySlider.Value=65;
                Check(((TextBox)Property("그림 투명도 (%)")!).Text=="65","Slider synchronizes the number");
                Check(canvas.Children.OfType<Border>().Single(x=>x.Tag is PdfEditElement item&&item.Id==stamp.Id).Child is Image {Opacity:.35},"Slider previews transparency while dragging");
                opacitySlider.RaiseEvent(new DragCompletedEventArgs(0,0,false){RoutedEvent=Thumb.DragCompletedEvent});
                Check(Stamp().ImageOpacity==.35,"Slider commits transparency at release");
                view.Undo();Check(Stamp().ImageOpacity==1,"Slider drag is undone in one step");
                var aspect = fields.Children.OfType<CheckBox>().Single(x=>Equals(x.Content,"가로세로 비율 고정"));
                Check(aspect.IsChecked==true && Stamp().LockImageAspectRatio,"Stamp aspect ratio is locked by default");
                var beforeResize=Stamp();
                aspect.IsChecked=false;
                aspect.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var widthBox=(TextBox)Property("너비 (pt)")!;
                widthBox.Text=(beforeResize.Width+12).ToString(System.Globalization.CultureInfo.CurrentCulture);
                widthBox.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(widthBox),Environment.TickCount,System.Windows.Input.Key.Enter){
                    RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent
                });
                Check(!Stamp().LockImageAspectRatio&&Math.Abs(Stamp().Width-beforeResize.Width-12)<.01&&Stamp().Height==beforeResize.Height,
                    "Unlocked stamp width does not change height");
                var freeStamp=Stamp();
                var movedStamp=Stamp();
                Call(view,"ResizeSelectionFrom",new List<PdfEditElement>{movedStamp},new[]{freeStamp},new Vector(15,7));
                Check(Math.Abs(movedStamp.Width-freeStamp.Width-15)<.01&&Math.Abs(movedStamp.Height-freeStamp.Height-7)<.01,
                    "Unlocked handle resizes axes independently");
                view.Undo();view.Undo();Check(Stamp().LockImageAspectRatio,"Undo restores aspect lock");
                var rotation=(ComboBox)Property("회전")!;
                Check(Property("색상")==null&&Property("너비 (pt)")!=null,"Stamp exposes only its applicable properties");
                rotation.SelectedItem=rotation.Items.OfType<ComboBoxItem>().Single(x=>Equals(x.Tag,"90"));
                Check(Stamp().Rotation==90,"Docked rotation applies immediately");
                view.Undo();Check(Stamp().Rotation==0,"Docked rotation is undoable");

                var planPage=view.CurrentPlan.Pages[0];
                var line=planPage.Elements.First(x=>x.Kind=="line");
                selected.Clear();selected.Add(line.Id);selected.Add(rectangle.Id);selectedId.SetValue(view,line.Id);Call(view,"DrawElements");
                Check(Property("선 종류")!=null&&Property("채움 색상")==null,"Mixed selection exposes only common fields");
                var color=(ComboBox)Property("색상")!;
                var white=color.Items.OfType<ComboBoxItem>().Single(x=>Equals(x.Tag,"#FFFFFF"));
                var swatch=((StackPanel)white.Content).Children.OfType<System.Windows.Shapes.Rectangle>().Single();
                Check(((SolidColorBrush)swatch.Fill).Color==Colors.White&&swatch.Stroke!=null,"White color sample has a visible outline");
                var lineStyle=(ComboBox)Property("선 종류")!;
                foreach(var (type,dashes) in new[]{("solid",0),("dotted",2),("dashed",2),("dashdot",4),("dashdotdot",6)})
                {
                    var option=lineStyle.Items.OfType<ComboBoxItem>().Single(x=>Equals(x.Tag,type));
                    var sample=((StackPanel)option.Content).Children.OfType<System.Windows.Shapes.Line>().Single();
                    Check(sample.StrokeDashArray.Count==dashes,"Line type sample matches "+type);
                }
                lineStyle.SelectedItem=lineStyle.Items.OfType<ComboBoxItem>().Single(x=>Equals(x.Tag,"dashdotdot"));
                Check(view.CurrentPlan.Pages[0].Elements.Where(x=>selected.Contains(x.Id)).All(x=>x.LineStyle=="dashdotdot"),
                    "Common properties apply to the entire selection");
                view.Undo();
                selected.Clear();selectedId.SetValue(view,"");Call(view,"DrawElements");
                Check(properties.IsVisible&&fields.Children.OfType<TextBlock>().Single().Text=="선택한 객체가 없습니다.",
                    "Clearing selection leaves properties open");
                selectedId.SetValue(view,rectangle.Id);Call(view,"DrawElements");
                var fill=(ComboBox)Property("채움 색상")!;
                fill.SelectedItem=fill.Items.OfType<ComboBoxItem>().Single(x=>Equals(x.Tag,"#1755B5"));
                var opacity=(TextBox)Property("채움 불투명도 (%)")!;
                opacity.Text="40";
                opacity.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window),0,System.Windows.Input.Key.Enter){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent});
                var shape=view.CurrentPlan.Pages[0].Elements.Single(x=>x.Id==rectangle.Id);
                Check(shape.Fill=="#1755B5"&&shape.Color==rectangle.Color&&shape.FillOpacity==.4,"Independent fill and opacity are applied");
                var shapeBorder=canvas.Children.OfType<Border>().Single(x=>x.Tag is PdfEditElement item&&item.Id==rectangle.Id);
                var shapeInk=((Canvas)shapeBorder.Child).Children.OfType<System.Windows.Shapes.Rectangle>().Single();
                Check(shapeInk.Fill.Opacity==.4&&shapeInk.Stroke.Opacity==1,"Canvas applies opacity to fill only");
                view.Undo();view.Undo();
                await view.FitUnifiedZoomAsync(false);
                var zoomBeforeClose=view.CurrentZoomPercent;
                Click((Button)view.FindName("ClosePropertiesButton"));
                Check(properties.Visibility==Visibility.Collapsed,"Close button hides the properties panel");
                selectedId.SetValue(view,stamp.Id);Call(view,"DrawElements");
                Check(properties.Visibility==Visibility.Collapsed,"Closed panel stays closed on selection");
                Call(view,"ToggleObjectProperties");
                await Task.Delay(300);
                Check(properties.IsVisible&&Math.Abs(view.CurrentZoomPercent-zoomBeforeClose)<.01,
                    "Menu reopens dock without changing zoom");
                var rotationSnapshot=Call(view,"Capture")!;
                var originalStamp=Stamp();
                var stampSelection=(List<PdfEditElement>)Call(view,"Objects")!;
                var pivot=new Point(originalStamp.X+originalStamp.Width/2,originalStamp.Y+originalStamp.Height/2);
                Check((bool)Call(view,"RotateSelectionFrom",stampSelection,new[]{originalStamp},pivot,37)!,"Free rotation fits");
                Call(view,"Commit",rotationSnapshot,false);
                Check(Stamp().Rotation==37&&Math.Abs(Stamp().X-originalStamp.X)<.001&&Math.Abs(Stamp().Y-originalStamp.Y)<.001,
                    "Free rotation keeps the center and records a non-right angle");
                view.Undo();Check(Stamp().Rotation==0,"Free rotation Undo");
                view.Redo();Check(Stamp().Rotation==37,"Free rotation Redo");
                var stampProject=Path.Combine(folder,id+"-stamp.topdf");
                await view.SaveProjectAsync(stampProject);
                await view.LoadProjectAsync(stampProject);
                Check(Math.Abs(Stamp().X-stamp.X-35)<.01&&Stamp().Rotation==37&&File.Exists(Stamp().ImagePath),"Moved and freely rotated stamp persists in reopened project");
                await view.SaveCopyAsync(Path.Combine(folder,id+"-stamp.pdf"));
                Console.WriteLine("PASS drawings and docked properties: stamp placement, move, undo/redo and export, eight tools, normalized gestures, font/alignment, group copy/paste, rotation, resize handle, z-order, undo/redo, zoom, editable project round trip/direct open, PDF export, original preserved. "+output);result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}
            finally{typeof(PdfEditorView).GetField("addedTextCommit",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window.View,null);typeof(PdfEditorView).GetField("dirty",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window.View,false);window.Close();app.Shutdown();}
        };app.Run();return result;
    }
}

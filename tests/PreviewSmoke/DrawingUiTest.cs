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
                    Call(view,"UpdateGraphic",new Point(130,90+n*65));Call(view,"UpdateGraphic",new Point(240,115+n*65));Call(view,"EndGraphic");
                }
                Check(view.CurrentPlan.Pages[0].Elements.Count==8,"All drawing tools");
                Check(view.RibbonButtons.Single(x=>x.IsChecked==true).Tag.ToString()=="12","Ribbon selection follows tool");
                var palette=(System.Windows.Controls.Primitives.UniformGrid)view.FindName("PenPalette");Check(palette.Children.Count==30,"Palette colors");
                palette.Children.OfType<RadioButton>().Single(x=>(string)x.Tag=="#1755B5").IsChecked=true;((Slider)view.FindName("PenWidthSlider")).Value=3.5;
                Check(view.CurrentPlan.Pages[0].Elements.Last().Color=="#1755B5" && view.CurrentPlan.Pages[0].Elements.Last().StrokeWidth==3.5,"Palette style applied");
                ((System.Windows.Controls.Primitives.ToggleButton)view.FindName("PenOptions")).IsChecked=true;await Task.Delay(100);
                var popup=(System.Windows.Controls.Primitives.Popup)view.FindName("PenPopup");var popupBody=(FrameworkElement)popup.Child;popupBody.UpdateLayout();
                var paletteBitmap=new RenderTargetBitmap((int)popupBody.ActualWidth,(int)popupBody.ActualHeight,96,96,PixelFormats.Pbgra32);paletteBitmap.Render(popupBody);
                var paletteEncoder=new PngBitmapEncoder();paletteEncoder.Frames.Add(BitmapFrame.Create(paletteBitmap));Directory.CreateDirectory("artifacts/drawing-test");using(var file=File.Create("artifacts/drawing-test/palette.png"))paletteEncoder.Save(file);
                ((System.Windows.Controls.Primitives.ToggleButton)view.FindName("PenOptions")).IsChecked=false;
                Check(view.CurrentPlan.Pages[0].Elements.All(x=>x.Points.All(p=>p.All(v=>v>=0&&v<=1))),"Normalized points");
                view.AddText(280,220,"직접 수정 · 가운데 정렬");
                ((ComboBox)view.FindName("TextFont")).SelectedIndex=2;((ComboBox)view.FindName("TextAlign")).SelectedIndex=1;
                Call(view,"ApplyStyle",view,new RoutedEventArgs());
                var selected=(HashSet<string>)typeof(PdfEditorView).GetField("selectedIds",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
                selected.UnionWith(view.CurrentPlan.Pages[0].Elements.Where(x=>x.Kind=="rectangle"||x.Kind=="ellipse").Select(x=>x.Id));
                view.CopyObjects();view.PasteObjects();Check(view.CurrentPlan.Pages[0].Elements.Count==12,"Group copy paste");
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
                var second=new PdfEditorWindow(project);second.Show();await second.Ready.WaitAsync(TimeSpan.FromSeconds(90));Check(second.CurrentPlan.Pages[0].Elements.Count==12,"Project direct open");second.Close();
                var main=new MainWindow();main.Show();await main.OpenFilesAsync([project]);Check(main.ActivePdfEditor?.CurrentPlan.Pages[0].Elements.Count==12,"Main file menu project route");main.Close();
                Console.WriteLine("PASS drawings: eight tools, normalized gestures, font/alignment, group copy/paste, rotation, resize handle, z-order, undo/redo, zoom, editable project round trip/direct open, PDF export, original preserved. "+output);result=0;
            }catch(Exception ex){Console.Error.WriteLine(ex);}
            finally{typeof(PdfEditorView).GetField("dirty",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(window.View,false);window.Close();app.Shutdown();}
        };app.Run();return result;
    }
}

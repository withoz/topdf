using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Topdf;
internal static class FontViewsUiTest
{
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 static async Task Until(Func<bool> predicate){using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(90));while(!predicate())await Task.Delay(100,ct.Token);}
 static IEnumerable<DependencyObject> Tree(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var x in Tree(child))yield return x;}}
 static void Capture(FrameworkElement control,string name){control.UpdateLayout();var bitmap=new RenderTargetBitmap((int)control.ActualWidth,(int)control.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(control);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(name);png.Save(stream);}
 public static int Run(string source)
 {
  int result=1;var app=new Application();app.Startup+=async(_,_)=>{
   var window=new MainWindow();window.Show();
   try{
    var input=Path.GetFullPath(source);var original=SHA256.HashData(File.ReadAllBytes(input));window.Add([input]);await Until(()=>window.ActivePdfEditor!=null);var view=window.ActivePdfEditor!;await view.Ready;
    Check(((FrameworkElement)view.FindName("PageNavigationPanel")).Visibility==Visibility.Collapsed,"Page panel hidden by default");
    var tool=view.RibbonButtons.Single(x=>x.Tag.ToString()=="0");tool.IsChecked=true;tool.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));await Until(()=>!view.IsViewing);
    var text=view.SourceTextItems.First(x=>x.Text.Contains("설계공모 공고문")||x.Text.Contains("123,000")||x.Text.Contains("설계용역"));Check(text.FontSize>0,"Actual transformed font size available");
    view.SelectSourceText(text.Id);window.UpdateLayout();var inline=((Canvas)view.FindName("EditCanvas")).Children.OfType<TextBox>().Single();Check(inline.FontSize==text.FontSize&&((SolidColorBrush)inline.Foreground).Color.A==0,"Original pixels retained beneath input");
    Check(text.CaretStops?.Length==text.Text.Length+1,"PDF glyph caret stops include final boundary");
    Check(!inline.CanUndo,"Opening original text must not add a text undo unit");
    window.Activate();inline.Focus();System.Windows.Input.Keyboard.Focus(inline);
    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Input);
    await Until(()=>inline.IsKeyboardFocused);
    inline.Select(3,0);window.UpdateLayout();
    var caret=((Canvas)view.FindName("EditCanvas")).Children.OfType<Canvas>().Single().Children.OfType<System.Windows.Shapes.Line>().Single();
    Check(Math.Abs(caret.X1-text.CaretStops![3][0])<.01&&Math.Abs(caret.Y1-(text.CaretStops[3][1]-text.FontSize*.86))<.01,"Caret follows PDF glyph origin and baseline");
    inline.SelectAll();
    var folder=Path.GetFullPath("artifacts/font-views-ui");Directory.CreateDirectory(folder);Capture(window,Path.Combine(folder,"selected-original.png"));
    var changed=text.Text.Contains("공고문")?text.Text.Replace("공고문","공고서"):text.Text.Contains("설계용역")?text.Text.Replace("신축공사","리모델링공사"):text.Text.Replace("123,000","923,000");await Until(()=>typeof(PdfEditorView).GetField("inlineCleanPage",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!=null);
    inline.Text=changed;
    var live=(Image)typeof(PdfEditorView).GetField("inlineLiveInk",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    Check(live.Source!=null,"Typing is visible synchronously before PDF worker returns");
    Capture(window,Path.Combine(folder,"instant-typing.png"));
    inline.Text=text.Text;Check(live.Source==null,"Reverting text restores original exact image");
    Check(((TextBox)view.FindName("EditText")).Visibility==Visibility.Collapsed,"Duplicate ribbon text field hidden");
    inline.Text=changed;Check(live.Source!=null,"Direct page input updates immediately");
    await Until(()=>live.Source==null);
    typeof(PdfEditorView).GetMethod("ApplyInlineText",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(view,null);await Until(()=>((Canvas)view.FindName("EditCanvas")).IsEnabled);
    var element=view.CurrentPlan.Pages[0].Elements.Single();Check(element.PreserveSourceStyle&&Math.Abs(element.FontSize-text.FontSize)<.01,"Original style retained in plan");
    view.SelectSourceText(text.Id);window.UpdateLayout();inline=((Canvas)view.FindName("EditCanvas")).Children.OfType<TextBox>().Single();Check(inline.Text==changed,"Replacement reopens with current text");
    ((ComboBox)view.FindName("TextAlign")).SelectedIndex=1;
    var formatted=view.CurrentPlan.Pages[0].Elements.Single();
    Check(formatted.Alignment=="center"&&!formatted.PreserveSourceStyle&&formatted.Text==changed,"Existing PDF text alignment applies immediately");
    await Until(()=>((Canvas)view.FindName("EditCanvas")).IsEnabled);
    view.Undo();
    await Until(()=>((Canvas)view.FindName("EditCanvas")).IsEnabled);
    Check(view.CurrentPlan.Pages[0].Elements.Single().PreserveSourceStyle,"Undo restores original PDF style");
    await view.ReturnToViewingAsync();await view.SetViewColumnsAsync(3);window.UpdateLayout();
    var spreads=(ListBox)view.FindName("SpreadPages");Check(view.IsViewing&&spreads.Visibility==Visibility.Visible&&view.ViewColumns==3,"Three-page viewer");
    Check(((IEnumerable)((IEnumerable)spreads.ItemsSource).Cast<object>().First()).Cast<object>().Count()==3,"Three pages in a row");
    await Until(()=>Tree(spreads).OfType<Image>().Count(x=>x.Source!=null)>=3);Capture(window,Path.Combine(folder,"three-pages.png"));
    typeof(PdfEditorView).GetMethod("TogglePagePane",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[view,new RoutedEventArgs()]);
    Check(((FrameworkElement)view.FindName("PageNavigationPanel")).Visibility==Visibility.Visible,"Thumbnail pane opens");
    var pages=(ListBox)view.FindName("EditPages");await Until(()=>Tree(pages).OfType<Image>().Any(x=>x.Source!=null));Capture(window,Path.Combine(folder,"thumbnails.png"));
    typeof(PdfEditorView).GetMethod("HidePagePane",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[view,new RoutedEventArgs()]);Check(((FrameworkElement)view.FindName("PageNavigationPanel")).Visibility==Visibility.Collapsed,"Pane closes");
    await view.SetViewColumnsAsync(2);Check(view.ViewColumns==2&&view.IsViewing,"Two-page viewer");await view.SetViewColumnsAsync(1);Check(view.ViewColumns==1&&view.IsViewing,"Native single-page viewer restored");
    var output=Path.Combine(folder,Guid.NewGuid()+".pdf");await view.SaveCopyAsync(output);Check(original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(input))),"Original unchanged");Console.WriteLine("PASS 17pt original-style input/save/re-edit, hidden/thumbnail/close sidebar, three/two/one page views, source preserved. "+output);result=0;
   }catch(Exception ex){Console.Error.WriteLine(ex);}
   finally{if(window.ActivePdfEditor is {} view){typeof(PdfEditorView).GetField("inlineText",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,null);typeof(PdfEditorView).GetField("selectedSource",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,null);typeof(PdfEditorView).GetField("dirty",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,false);}window.Close();app.Shutdown();}
  };app.Run();return result;
 }
}

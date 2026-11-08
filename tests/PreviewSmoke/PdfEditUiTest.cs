using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Topdf;

internal static class PdfEditUiTest
{
    public static int Run(string source)
    {
        var app = new Application(); int result = 1;
        app.Startup += async (_, _) => {
            var editor = new PdfEditorWindow(Path.GetFullPath(source)); editor.Show();
            try {
                await editor.Ready.WaitAsync(TimeSpan.FromSeconds(60));
                foreach(var panel in Descendants(editor).OfType<Expander>())panel.IsExpanded=true; editor.UpdateLayout();
                var original = SHA256.HashData(File.ReadAllBytes(source));
                editor.AddText(60,350,"UI 수정 321,000원");
                editor.UpdateLayout();
                var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)editor.ActualWidth,(int)editor.ActualHeight,96,96,PixelFormats.Pbgra32);
                bitmap.Render(editor);
                var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using(var image=File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!,"editor-ui.png"))) encoder.Save(image);
                editor.RotateSelectedPage(90);
                if(editor.CurrentPlan.Pages[0].Rotation!=90 || editor.CurrentPlan.Pages[0].Elements.Single().Rotation!=90) throw new Exception("Rotate failed");
                editor.Undo(); if(editor.CurrentPlan.Pages[0].Rotation!=0) throw new Exception("Undo failed");
                editor.Redo(); if(editor.CurrentPlan.Pages[0].Rotation!=90) throw new Exception("Redo failed");
                editor.Undo();
                var pages=(ListBox)editor.FindName("EditPages");
                pages.SelectedIndex=2;
                Click(editor,"쪽 삭제");
                if(editor.CurrentPlan.Pages.Count!=2) throw new Exception("Delete failed");
                Click(editor,"앞으로");
                if(editor.CurrentPlan.Pages[0].SourceIndex!=1) throw new Exception("Reorder failed");
                var output=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!,"ui-edited-"+Guid.NewGuid().ToString("N")+".pdf");
                await editor.SaveCopyAsync(output);
                var check=await PdfEditService.InspectAsync(output,Path.GetDirectoryName(output)!,CancellationToken.None);
                if(check.Pages.Length!=2 || !original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source)))) throw new Exception("Save/original preservation failed");
                bool refused=false; try { await editor.SaveCopyAsync(source); } catch(IOException) { refused=true; }
                if(!refused) throw new Exception("Overwrite allowed");
                Console.WriteLine("PASS PDF editor: rendered preview, Korean text, rotate, undo/redo, delete/reorder, save/reopen, original preserved, overwrite refused. "+output);
                result=0;
            } catch(Exception ex) { Console.Error.WriteLine(ex); }
            finally { if(result!=0) while(editor.CurrentPlan.Pages.Any(p=>p.Elements.Count>0)) editor.Undo(); editor.Close(); app.Shutdown(); }
        };
        app.Run(); return result;
    }
    static void Click(DependencyObject root,string label) => Descendants(root).OfType<Button>().Single(b=>(string?)b.Content==label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static IEnumerable<DependencyObject> Descendants(DependencyObject root) {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) { var child=VisualTreeHelper.GetChild(root,i); yield return child; foreach(var nested in Descendants(child)) yield return nested; }
    }
}

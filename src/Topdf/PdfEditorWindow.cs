using System.Windows;

namespace Topdf;

// Retained as a standalone host for focused integration tests.
public class PdfEditorWindow : Window
{
    public PdfEditorView View { get; }
    public PdfEditorWindow(string pdf, string? name = null) {
        Title = "TOPDF · PDF 수정"; Width = 1160; Height = 820; MinWidth = 900; MinHeight = 600;
        View = new PdfEditorView(pdf,name); Content = View;
        View.Saved += (_,_) => Close();
        Closing += (_,e) => e.Cancel = !View.CanClose();
        Closed += (_,_) => View.DisposeEditor();
    }
    public Task Ready => View.Ready;
    public PdfEditPlan CurrentPlan => View.CurrentPlan;
    public string? ResultPath => View.ResultPath;
    public new object FindName(string name) => View.FindName(name);
    public void AddText(double x,double y,string text,bool note=false) => View.AddText(x,y,text,note);
    public void RotateSelectedPage(int degrees) => View.RotateSelectedPage(degrees);
    public void Undo() => View.Undo();
    public void Redo() => View.Redo();
    public Task SaveCopyAsync(string target) => View.SaveCopyAsync(target);
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
namespace Topdf;
public partial class PdfEditorView
{
    Action? cancelEditGesture;
    KeyBinding? redoAlias;
    void InitializeEditCommands()
    {
        foreach(var command in new RoutedCommand[]{ApplicationCommands.Undo,ApplicationCommands.Redo,ApplicationCommands.Copy,ApplicationCommands.Cut,ApplicationCommands.Paste,ApplicationCommands.Delete,ApplicationCommands.SelectAll,ApplicationCommands.Save,ApplicationCommands.SaveAs})
            CommandBindings.Add(new CommandBinding(command,ExecuteEditCommand,CanExecuteEditCommand));
        redoAlias=new KeyBinding(ApplicationCommands.Redo,new KeyGesture(Key.Z,ModifierKeys.Control|ModifierKeys.Shift)){CommandTarget=EditCanvas};InputBindings.Add(redoAlias);
        InputBindings.Add(new KeyBinding(ApplicationCommands.SaveAs,new KeyGesture(Key.S,ModifierKeys.Control|ModifierKeys.Shift)));
        UndoButton.CommandTarget=RedoButton.CommandTarget=EditCanvas;
        AddHandler(Keyboard.GotKeyboardFocusEvent,new KeyboardFocusChangedEventHandler((_,e)=>{
            var target=IsTextCommandTarget(e.NewFocus)?e.NewFocus:EditCanvas;
            UndoButton.CommandTarget=RedoButton.CommandTarget=target;if(redoAlias!=null)redoAlias.CommandTarget=target;CommandManager.InvalidateRequerySuggested();
        }),true);
    }
    static bool IsTextCommandTarget(object? source)
    {
        for(var node=source as DependencyObject;node!=null;node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node))
            if(node is TextBoxBase)return true;
        return false;
    }
    void CanExecuteEditCommand(object sender,CanExecuteRoutedEventArgs e)
    {
        var gesture=drawing!=null||dragging!=null||cancelEditGesture!=null||selectionBox!=null;
        if(e.Command==ApplicationCommands.Save||e.Command==ApplicationCommands.SaveAs){e.CanExecute=info!=null&&!IsBusy&&!gesture;e.Handled=true;return;}
        if(IsTextCommandTarget(e.OriginalSource)){e.CanExecute=false;e.Handled=true;return;}
        if(IsViewing){e.ContinueRouting=true;return;}
        var ready=info!=null&&Page!=null&&!IsBusy;
        if(e.Command==ApplicationCommands.Undo)e.CanExecute=ready&&(drawing!=null||dragging!=null||cancelEditGesture!=null||selectionBox!=null||undo.Count>0);
        else if(e.Command==ApplicationCommands.Redo)e.CanExecute=ready&&!gesture&&redo.Count>0;
        else if(e.Command==ApplicationCommands.Paste)e.CanExecute=ready&&!gesture&&copied.Count>0;
        else if(e.Command==ApplicationCommands.SelectAll)e.CanExecute=ready&&!gesture&&Page!.Elements.Count>0;
        else e.CanExecute=ready&&!gesture&&Objects().Count>0;
        e.Handled=true;
    }
    void ExecuteEditCommand(object sender,ExecutedRoutedEventArgs e)
    {
        if(e.Command==ApplicationCommands.Save||e.Command==ApplicationCommands.SaveAs){SaveEditedPdf(sender,new RoutedEventArgs());e.Handled=true;return;}
        if(IsTextCommandTarget(e.OriginalSource)||IsViewing||IsBusy||Page==null)return;
        if(e.Command==ApplicationCommands.Undo)Undo();
        else if(e.Command==ApplicationCommands.Redo)Redo();
        else if(e.Command==ApplicationCommands.Copy){ApplyInlineText();CopyObjects();}
        else if(e.Command==ApplicationCommands.Cut){ApplyInlineText();CopyObjects();DeleteElement(sender,new RoutedEventArgs());}
        else if(e.Command==ApplicationCommands.Paste){ApplyInlineText();PasteObjects();}
        else if(e.Command==ApplicationCommands.Delete){ApplyInlineText();DeleteElement(sender,new RoutedEventArgs());}
        else if(e.Command==ApplicationCommands.SelectAll){ApplyInlineText();selectedIds.Clear();selectedIds.UnionWith(Page.Elements.Select(x=>x.Id));selectedId=selectedIds.FirstOrDefault()??"";selectedSource=null;EditTool.SelectedIndex=1;DrawElements();}
        e.Handled=true;CommandManager.InvalidateRequerySuggested();
    }
    void AnnotationContextOpening(object sender,ContextMenuEventArgs e)
    {
        if(IsTextCommandTarget(e.OriginalSource))return;
        if(IsViewing||IsBusy||Page==null){e.Handled=true;return;}
        if(drawing!=null)DiscardGraphic();
        ApplyInlineText();
        if(e.CursorLeft>=0&&e.CursorTop>=0){
            var hit=AnnotationAt(Mouse.GetPosition(EditCanvas),false);
            if(hit!=null&&!Objects().Any(x=>x.Id==hit.Id)){selectedIds.Clear();selectedIds.Add(hit.Id);selectedId=hit.Id;selectedSource=null;DrawElements();}
        }
        Keyboard.Focus(EditCanvas);CommandManager.InvalidateRequerySuggested();
    }
    bool CancelPendingEditGesture()
    {
        if(cancelEditGesture!=null){var cancel=cancelEditGesture;cancelEditGesture=null;cancel();return true;}
        if(dragging!=null){
            var before=dragBefore;dragging=null;dragVisual=null;dragBefore=null;EditCanvas.ReleaseMouseCapture();
            if(before!=null){Restore(before);dirty=!IsClean();}return true;
        }
        return false;
    }
}

using System.Windows;
using System.Windows.Controls;

namespace Topdf;

public partial class PdfEditorView
{
    sealed record VisiblePdfNote(int Page,string Header,string Text);

    void RefreshPdfNotes(bool openIfPresent=false)
    {
        if(info==null)return;
        var notes=new List<VisiblePdfNote>();
        for(var index=0;index<plan.Pages.Count;index++){
            var number=index+1;
            var page=plan.Pages[index];
            foreach(var note in info.Pages[page.SourceIndex].Notes){
                var header=string.IsNullOrWhiteSpace(note.Author)?$"{number}쪽":$"{number}쪽 · {note.Author}";
                notes.Add(new(number,header,note.Text));
            }
            foreach(var note in page.Elements.Where(item=>item.Kind=="note"))
                notes.Add(new(number,$"{number}쪽",note.Text));
        }
        PdfNotesList.ItemsSource=notes;
        if(openIfPresent&&notes.Count>0)PdfNotesPane.Visibility=Visibility.Visible;
    }

    internal void TogglePdfNotes(object sender,RoutedEventArgs e)
    {
        var visible=PdfNotesPane.Visibility==Visibility.Visible;
        if(!visible)RefreshPdfNotes();
        PdfNotesPane.Visibility=visible?Visibility.Collapsed:Visibility.Visible;
    }

    async void NavigateToPdfNote(object sender,SelectionChangedEventArgs e)
    {
        if(PdfNotesList.SelectedItem is not VisiblePdfNote note)return;
        PdfNotesList.SelectedIndex=-1;
        await NavigateToPageAsync(note.Page);
    }
}

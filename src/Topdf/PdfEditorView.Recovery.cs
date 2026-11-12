using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Topdf;

public partial class PdfEditorView
{
    internal static string RecoveryFolder => Path.Combine(AppPaths.DataRoot,"recovery");
    readonly DispatcherTimer recoveryTimer=new(){Interval=TimeSpan.FromSeconds(15)};
    string? recoveryFile;
    long editRevision;
    bool recoveryInProgress;
    public bool HasUnsavedEdits=>dirty;
    public event EventHandler? EditStateChanged;

    void SetDirty(bool value)
    {
        editRevision++;
        if(PdfNotesPane.Visibility==Visibility.Visible)RefreshPdfNotes();
        if(dirty!=value){dirty=value;EditStateChanged?.Invoke(this,EventArgs.Empty);}
        if(!value){StopRecovery();return;}
        if(recoveryFile!=null)return;
        var name=Path.GetFileNameWithoutExtension(suggestedName);
        recoveryFile=Path.Combine(RecoveryFolder,name+"-"+Guid.NewGuid().ToString("N")[..8]+".topdf");
        recoveryTimer.Tick+=SaveRecoveryTick;
        recoveryTimer.Start();
    }

    long recoveredRevision;
    async void SaveRecoveryTick(object? sender,EventArgs e)
    {
        if(!dirty||IsBusy||drawing!=null||dragging!=null||cancelEditGesture!=null||addedTextCommit!=null||inlineText!=null||recoveryInProgress||recoveredRevision==editRevision||recoveryFile==null)return;
        var revision=editRevision;
        var destination=recoveryFile;
        var temp=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        recoveryInProgress=true;
        try {
            Directory.CreateDirectory(RecoveryFolder);
            await WriteProjectArchiveAsync(temp,snapshot,PdfEditService.Clone(plan),lifetime.Token);
            if(dirty&&destination==recoveryFile&&!lifetime.IsCancellationRequested) {
                File.Move(temp,destination,true);
                recoveredRevision=revision;
            }
        } catch(OperationCanceledException) {}
        catch(IOException) {}
        catch(UnauthorizedAccessException) {}
        finally { recoveryInProgress=false;TryRemoveRecoveryFile(temp); }
    }

    void StopRecovery()
    {
        recoveryTimer.Stop();
        recoveryTimer.Tick-=SaveRecoveryTick;
        if(recoveryFile!=null)TryRemoveRecoveryFile(recoveryFile);
        recoveryFile=null;
    }

    static void TryRemoveRecoveryFile(string path)
    {
        try { File.Delete(path); }
        catch(IOException) {}
        catch(UnauthorizedAccessException) {}
    }
}

public partial class MainWindow
{
    async void OpenRecovery(object sender,RoutedEventArgs e)
    {
        var directory=PdfEditorView.RecoveryFolder;
        if(!Directory.Exists(directory)||!Directory.EnumerateFiles(directory,"*.topdf").Any()) {
            Summary.Text="복구할 편집 파일이 없습니다.";
            return;
        }
        var dialog=new OpenFileDialog{Title="복구 파일 열기",InitialDirectory=directory,Filter="TOPDF 복구 파일|*.topdf"};
        if(dialog.ShowDialog(this)==true)await OpenFilesAsync([dialog.FileName]);
    }
}

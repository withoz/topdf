using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace Topdf;

public sealed class FileJob : INotifyPropertyChanged
{
    public required string Source { get; init; }
    public string Name => Path.GetFileName(Source);
    public string? Pdf { get; set; }
    string status = "대기", detail = "";
    bool isChecked;
    string quality = "";
    public DocumentAnalysis? Analysis { get; set; }
    public string? AnalysisError { get; set; }
    public string Quality { get => quality; set { quality = value; Changed(nameof(Quality)); } }
    public bool IsChecked { get => isChecked; set { isChecked = value; Changed(nameof(IsChecked)); } }
    public string Status { get => status; set { status = value; Changed(nameof(Status)); } }
    public string Detail { get => detail; set { detail = value; Changed(nameof(Detail)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
}

public partial class MainWindow : Window
{
    readonly ObservableCollection<FileJob> jobs = new();
    readonly ConversionService converter = new();
    CancellationTokenSource? active;
    Task? viewerInitialization;
    Task? conversionTask;
    long previewVersion;
    string? allowedPreviewUri;
    ulong previewNavigation;
    bool closed;
    bool exporting;
    bool fullscreen;
    WindowState previousState;
    WindowStyle previousStyle;
    ResizeMode previousResizeMode;
    public MainWindow() { InitializeComponent(); Files.ItemsSource = jobs; DocumentTabs.ItemsSource = jobs; Closing += (_, e) => { if (pdfEditor != null && !pdfEditor.CanClose()) { e.Cancel = true; return; } if (active != null) { active.Cancel(); e.Cancel = true; Summary.Text = "변환 취소 중입니다. 완료 후 창을 닫아 주세요."; } if (exporting) { e.Cancel = true; Summary.Text = "PDF 저장 중입니다. 완료 후 창을 닫아 주세요."; } }; Closed += (_, _) => { closed = true; pdfEditor?.DisposeEditor(); lifetime.Cancel(); LocalAiHost.Stop(); folderAutomation?.Dispose(); previewVersion++; PdfViewer.Dispose(); }; }
    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
            if (!jobs.Any(j => j.Source.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                var job = new FileJob { Source = path, Pdf = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? path : null, Status = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "완료" : !ConversionService.Supports(path) ? "미지원" : CompatibilityMode.IsChecked != true && !IndependentConversion.Supports(path) ? "호환 필요" : "대기", Detail = EngineLabel(path) };
                job.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(FileJob.IsChecked)) UpdateCheckedCount(); };
                jobs.Add(job);
            }
        Summary.Text = $"{jobs.Count}개 파일 · {(CompatibilityMode.IsChecked == true ? "설치형 호환 모드" : "오픈소스 독립 변환 모드")}";
        if (Files.SelectedItem == null && jobs.Count > 0) Files.SelectedItem = jobs[0];
        SetMenuOpen(Files.SelectedItem is not FileJob { Pdf: not null });
    }
    public void SetMenuOpen(bool open) => MenuToggle.IsChecked = open;
    void OpenMenu(object sender, RoutedEventArgs e) => SetMenuOpen(true);
    void CloseMenu(object sender, RoutedEventArgs e) => SetMenuOpen(false);
    void HandleShortcuts(object sender, KeyEventArgs e)
    {
        if(pdfEditor!=null && e.Key!=Key.F11 && !(e.Key==Key.M && Keyboard.Modifiers==ModifierKeys.Control))return;
        if (e.Key == Key.F11) { ToggleFullscreen(sender, e); e.Handled = true; }
        else if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.Control) { SetMenuOpen(MenuToggle.IsChecked != true); e.Handled = true; }
        else if (e.Key == Key.Escape) {
            if (SmartToggle.IsChecked == true) SmartToggle.IsChecked = false;
            else if (MenuToggle.IsChecked == true) SetMenuOpen(false);
            else if (fullscreen) ToggleFullscreen(sender, e);
            e.Handled = true;
        }
    }
    void ToggleFullscreen(object sender, RoutedEventArgs e)
    {
        SetMenuOpen(false);
        if (!fullscreen) {
            previousState = WindowState; previousStyle = WindowStyle; previousResizeMode = ResizeMode;
            WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
        } else {
            WindowState = WindowState.Normal; WindowStyle = previousStyle; ResizeMode = previousResizeMode; WindowState = previousState;
        }
        fullscreen = !fullscreen;
        FullscreenButton.Content = fullscreen ? "창 모드  F11" : "전체 화면  F11";
    }
    static T? ParentOf<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null) {
            if (element is T found) return found;
            element = element is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(element);
        }
        return null;
    }
    void PreviewRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (ParentOf<CheckBox>(e.OriginalSource as DependencyObject) != null) return;
        if (ParentOf<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is FileJob { Pdf: not null }) SetMenuOpen(false);
    }
    async void AddFiles(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Multiselect = true, Filter = "모든 파일|*.*" }; if (dialog.ShowDialog() == true) await OpenFilesAsync(dialog.FileNames); }
    async void FilesDropped(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await OpenFilesAsync(paths); }
    async void ConvertFiles(object sender, RoutedEventArgs e) => await ConvertPendingAsync();
    public Task OpenFromShellAsync(IEnumerable<string> arguments) => OpenFilesAsync(arguments.Where(p => !p.StartsWith("--", StringComparison.Ordinal)));
    public async Task OpenFilesAsync(IEnumerable<string> arguments)
    {
        var paths = arguments.Where(File.Exists).Select(Path.GetFullPath).ToArray();
        if (paths.Length == 0) { Summary.Text = "열 파일을 찾을 수 없습니다."; return; }
        var project=paths.FirstOrDefault(p=>Path.GetExtension(p).Equals(".topdf",StringComparison.OrdinalIgnoreCase));
        var documents=paths.Where(p=>!Path.GetExtension(p).Equals(".topdf",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(documents.Length>0) Add(documents);
        if(project!=null){Add([project]);var projectJob=jobs.First(j=>j.Source.Equals(project,StringComparison.OrdinalIgnoreCase));projectJob.Status="편집";Files.SelectedItem=projectJob;await ShowPreviewAsync(projectJob);if(pdfEditor!=null){try{await pdfEditor.Ready;while(pdfEditor?.IsBusy==true)await Task.Delay(20,lifetime.Token);}catch(Exception ex){Summary.Text=ex.Message;}}return;}
        Files.SelectedItem = jobs.First(j => j.Source.Equals(paths[0], StringComparison.OrdinalIgnoreCase));
        await ConvertPendingAsync();
        await Task.WhenAll(jobs.Where(j => j.Pdf != null).Select(InspectAsync).ToArray());
    }
    void CloseDocument(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileJob job }) return;
        if (job.Status == "변환 중") { Summary.Text = "변환 취소 후 탭을 닫아 주세요."; return; }
        if(ReferenceEquals(job,editorJob)){if(pdfEditor!=null&&!pdfEditor.CanClose())return;EndPdfEditing();}
        var index = jobs.IndexOf(job);
        var selected = ReferenceEquals(Files.SelectedItem, job);
        jobs.Remove(job);
        if (selected && jobs.Count > 0) Files.SelectedItem = jobs[Math.Min(index, jobs.Count - 1)];
        if (jobs.Count == 0) { _ = ShowPreviewAsync(null); SetMenuOpen(false); }
        UpdateCheckedCount();
        e.Handled = true;
    }
    void RegisterFileAssociations(object sender, RoutedEventArgs e)
    {
        try {
            var executable = Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
            FileAssociationRegistration.RegisterCurrentUser(executable);
            MessageBox.Show(this, "TOPDF를 연결 프로그램 목록에 등록했습니다.\n\n탐색기에서 파일 우클릭 → 연결 프로그램 → TOPDF를 선택하세요. 목록에 없으면 '다른 앱 선택'에서 TOPDF를 선택하세요.\n\n기본 앱으로 사용하려면 Windows의 '항상' 또는 '항상 이 앱 사용'을 직접 선택하세요. 앱 폴더를 옮긴 경우 다시 등록해 주세요.", "연결 프로그램 등록 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Summary.Text = "연결 프로그램 등록 실패: " + ex.Message; }
    }
    public Task ConvertPendingAsync()
    {
        if (conversionTask is { IsCompleted: false }) return conversionTask;
        return conversionTask = ConvertQueueAsync();
    }
    async Task ConvertQueueAsync()
    {
        if (active != null) return;
        active = new();
        var compatibility = CompatibilityMode.IsChecked == true;
        CompatibilityMode.IsEnabled = false;
        try
        {
            var attempted = new HashSet<FileJob>();
            while (jobs.FirstOrDefault(j => !attempted.Contains(j) && j.Status is "대기" or "실패" or "취소") is { } job)
            {
                attempted.Add(job);
                if (active.IsCancellationRequested) break;
                job.Status = "변환 중"; job.Detail = "";
                try {
                    job.Analysis = null; job.AnalysisError = null;
                    job.Pdf = await converter.ConvertAsync(job.Source, AppPaths.Cache, active.Token, compatibility);
                    job.Status = "완료"; job.Detail = EngineLabel(job.Source);
                    if (ReferenceEquals(Files.SelectedItem, job)) { await ShowPreviewAsync(job); SetMenuOpen(false); }
                    _ = InspectAsync(job);
                }
                catch (OperationCanceledException) { job.Status = "취소"; break; }
                catch (Exception ex) { job.Status = "실패"; job.Detail = ex.Message; if (ReferenceEquals(Files.SelectedItem, job)) await ShowPreviewAsync(job); }
            }
            Summary.Text = $"완료 {jobs.Count(j => j.Status == "완료")}개 · 실패 {jobs.Count(j => j.Status == "실패")}개 · 호환 필요 {jobs.Count(j => j.Status == "호환 필요")}개";
        }
        finally { active.Dispose(); active = null; CompatibilityMode.IsEnabled = true; }
    }
    string EngineLabel(string path) => CompatibilityMode.IsChecked == true && Path.GetExtension(path).ToLowerInvariant() is not ".pdf" and not ".dxf" ? "설치형 호환 엔진" : IndependentConversion.EngineName(path);
    void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (Files == null || Summary == null) return;
        foreach (var job in jobs.Where(j => ConversionService.Supports(j.Source) && !Path.GetExtension(j.Source).Equals(".pdf", StringComparison.OrdinalIgnoreCase))) {
            job.Pdf = null; job.Analysis = null; job.AnalysisError = null; job.Quality = "";
            job.Detail = EngineLabel(job.Source);
            job.Status = CompatibilityMode.IsChecked != true && !IndependentConversion.Supports(job.Source) ? "호환 필요" : "대기";
        }
        Summary.Text = CompatibilityMode.IsChecked == true ? "설치형 호환 모드: 기존 프로그램으로 변환합니다." : "오픈소스 모드: HWP·DOCX·XLS·XLSX·DWG·DXF를 독립 변환합니다. DOC/PPT 등 검증 보류 형식은 호환 모드가 필요합니다.";
        _ = ShowPreviewAsync(Files.SelectedItem as FileJob);
    }
    void CancelConversion(object sender, RoutedEventArgs e) => active?.Cancel();
    async void SelectionChanged(object sender, SelectionChangedEventArgs e) { await ShowPreviewAsync(Files.SelectedItem as FileJob); RefreshInspection(); }

    void PreviewNotice(string message)
    {
        PdfViewer.Visibility = Visibility.Hidden;
        PreviewMessage.Text = message;
        PreviewMessage.Visibility = Visibility.Visible;
        EmptyPreview.Visibility = Visibility.Visible;
    }

    async Task InitializeViewerAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.Viewer,new CoreWebView2EnvironmentOptions {AdditionalBrowserArguments="--force-renderer-accessibility"});
        if (closed) return;
        await PdfViewer.EnsureCoreWebView2Async(environment);
        if (closed) return;
        var core = PdfViewer.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NavigationStarting += (_, e) => {
            if (!string.Equals(e.Uri.Split('#')[0], allowedPreviewUri, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
            else previewNavigation = e.NavigationId;
        };
        core.NavigationCompleted += (_, e) => {
            if (!e.IsSuccess && e.NavigationId == previewNavigation && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && !closed && PdfViewer.Visibility == Visibility.Visible)
                PreviewNotice("PDF를 표시할 수 없습니다. 파일 상태를 확인하거나 외부 앱으로 열어 주세요.");
        };
        core.ProcessFailed += (_, _) => {
            if (!closed) PreviewNotice("미리보기 프로세스가 종료되었습니다. 앱을 다시 실행해 주세요.");
        };
    }

    async Task ShowPreviewAsync(FileJob? job)
    {
        var documentPath=EditorPath(job);
        if(pdfEditor!=null && ReferenceEquals(job,editorJob) && string.Equals(documentPath,editorSource,StringComparison.OrdinalIgnoreCase))return;
        if(!CanChangeDocument(job))return;
        var version = ++previewVersion;
        if(job!=null && Path.GetExtension(job.Source).Equals(".topdf",StringComparison.OrdinalIgnoreCase)){PreviewTitle.Text=job.Name;await StartPdfEditorAsync(job.Source,job.Name);return;}
        PreviewTitle.Text = job == null ? "PDF 미리보기" : job.Name;
        if (job?.Pdf == null) {
            PreviewNotice(job == null ? "PDF 또는 변환이 완료된 파일을 선택하세요." : job.Status == "미지원" ? "이 형식은 아직 변환 엔진이 연결되지 않았습니다." : job.Status == "호환 필요" ? "독립 변환 품질 검증을 통과하지 못한 형식입니다. 설치형 호환 모드에서 기존 엔진을 사용할 수 있습니다." : job.Status == "실패" ? "변환에 실패했습니다. " + job.Detail : "변환이 완료되면 여기에 PDF가 표시됩니다.");
            return;
        }
        PreviewNotice("PDF를 불러오는 중입니다…");
        try {
            var path = Path.GetFullPath(job.Pdf);
            if (!File.Exists(path)) throw new FileNotFoundException("PDF 파일을 찾을 수 없습니다. 다시 변환해 주세요.");
            viewerInitialization ??= InitializeViewerAsync();
            await viewerInitialization;
            if (closed || version != previewVersion || pdfEditor != null) return;
            allowedPreviewUri = new Uri(path).AbsoluteUri;
            PdfViewer.CoreWebView2.Navigate(allowedPreviewUri);
            PreviewMessage.Visibility = Visibility.Collapsed;
            EmptyPreview.Visibility = Visibility.Collapsed;
            PdfViewer.Visibility = Visibility.Visible;
            await StartPdfEditorAsync(path,job.Name);
        }
        catch (Exception ex) {
            if (closed || version != previewVersion || pdfEditor != null) return;
            viewerInitialization = null;
            PreviewNotice(ex is WebView2RuntimeNotFoundException
                ? "미리보기에 Microsoft Edge WebView2 Runtime이 필요합니다. 설치 후 앱을 다시 실행하세요. 외부 앱으로 열기는 계속 사용할 수 있습니다."
                : "미리보기를 열 수 없습니다. " + ex.Message);
        }
    }
    async void OpenPdf(object sender, RoutedEventArgs e)
    {
        if (Files.SelectedItem is not FileJob { Pdf: { } pdf }) { Summary.Text = "변환이 완료된 파일을 선택해 주세요."; return; }
        try { if(pdfEditor?.HasPlanChanges==true)pdf=await pdfEditor.GetViewingPdfAsync();Process.Start(new ProcessStartInfo(pdf) { UseShellExecute = true }); } catch (Exception ex) { Summary.Text = ex.Message; }
    }
    void UpdateCheckedCount() => SaveCheckedButton.Content = $"선택 저장 ({jobs.Count(j => j.IsChecked)})";
    void CheckAll(object sender, RoutedEventArgs e) { foreach (var job in jobs) job.IsChecked = true; }
    void UncheckAll(object sender, RoutedEventArgs e) { foreach (var job in jobs) job.IsChecked = false; }
    async void SaveCheckedPdfs(object sender, RoutedEventArgs e)
    {
        var selected = jobs.Where(j => j.IsChecked).Select(j => new PdfExportItem(j.Source, j.Pdf)).ToArray();
        if (selected.Length == 0) { Summary.Text = "왼쪽 저장 체크박스에서 저장할 파일을 선택해 주세요."; return; }
        if (selected.All(j => j.Pdf == null)) { Summary.Text = "체크한 파일의 변환을 먼저 완료해 주세요."; return; }
        var dialog = new OpenFolderDialog { Title = "체크한 PDF를 저장할 폴더 선택" };
        if (dialog.ShowDialog(this) != true) return;
        exporting = true;
        SaveCheckedButton.IsEnabled = false;
        try {
            if(pdfEditor!=null && editorJob?.IsChecked==true && pdfEditor.HasPlanChanges) {
                var current=await pdfEditor.GetViewingPdfAsync();selected=selected.Select(x=>x.Source==editorJob.Source?x with {Pdf=current}:x).ToArray();
            }
            var result = await Task.Run(() => PdfExportService.Export(selected, dialog.FolderName));
            Summary.Text = $"PDF {result.Saved.Count}개 저장 완료 · 변환 전 {result.Skipped.Count}개 제외 · 실패 {result.Errors.Count}개\n{dialog.FolderName}";
            if (result.Errors.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, result.Errors), "일부 PDF 저장 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { Summary.Text = "PDF 저장 실패: " + ex.Message; }
        finally { exporting = false; SaveCheckedButton.IsEnabled = true; }
    }
    void SavePdf(object sender, RoutedEventArgs e)
    {
        if(pdfEditor!=null && pdfEditor.HasPlanChanges){pdfEditor.RequestPdfSave();return;}
        if (Files.SelectedItem is not FileJob { Pdf: { } pdf } job) { Summary.Text = "변환이 완료된 파일을 선택해 주세요."; return; }
        var dialog = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = Path.GetFileNameWithoutExtension(job.Source) + ".pdf" };
        if (dialog.ShowDialog() != true) return;
        try {
            if (Path.GetFullPath(dialog.FileName).Equals(Path.GetFullPath(job.Source), StringComparison.OrdinalIgnoreCase)) throw new IOException("원본 파일과 다른 저장 위치를 선택하세요.");
            File.Copy(pdf, dialog.FileName, true); Summary.Text = "PDF 저장 완료";
        } catch (Exception ex) { Summary.Text = ex.Message; }
    }
}

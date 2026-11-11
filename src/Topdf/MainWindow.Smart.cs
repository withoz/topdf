using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace Topdf;

public partial class MainWindow
{
    readonly CancellationTokenSource lifetime = new();
    readonly Dictionary<(FileJob Job, string Pdf), Task> analyzing = new();
    readonly SemaphoreSlim smartWork = new(1);
    FolderAutomation? folderAutomation;
    string? watchedFolder, exportFolder;
    FileJob? SelectedJob => Files.SelectedItem as FileJob;

    public Task InspectAsync(FileJob job)
    {
        if (!BuildFeatures.SmartEnabled || job.Pdf == null || job.Analysis != null || job.AnalysisError != null) return Task.CompletedTask;
        var key = (job, job.Pdf);
        if (analyzing.TryGetValue(key, out var running)) return running;
        var task = InspectCoreAsync(job);
        analyzing[key] = task;
        _ = task.ContinueWith(_ => Dispatcher.InvokeAsync(() => analyzing.Remove(key)), TaskScheduler.Default);
        return task;
    }
    async Task InspectCoreAsync(FileJob job)
    {
        var pdf = job.Pdf;
        job.Quality = "점검 중";
        try {
            var analysis = await DocumentIntelligence.AnalyzeAsync(job.Source, pdf!, lifetime.Token);
            if (job.Pdf != pdf || closed) return;
            job.Analysis = analysis;
            job.Quality = analysis.Issues.Length > 0 ? $"확인 {analysis.Issues.Length}" : "의심 없음";
        } catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && job.Pdf == pdf) { job.Quality = "점검 보류"; job.AnalysisError = ex.Message; } }
        if (!closed && ReferenceEquals(SelectedJob, job)) RefreshInspection();
    }
    void RefreshInspection()
    {
        if (!BuildFeatures.SmartEnabled || QualityList == null) return;
        QualityList.ItemsSource = SelectedJob?.Analysis?.Issues.Select(i => new Evidence(SelectedJob, i.Page, i.Message)).ToArray();
        QualityNotice.Text = SelectedJob?.Analysis is { } a ? $"{a.Pages.Length}쪽 · {a.Issues.Length}건 확인\n{a.Notice}" : SelectedJob?.AnalysisError ?? "문서 점검을 누르면 확인할 항목을 표시합니다.";
        CompareTarget.ItemsSource = jobs.Where(j => j.Pdf != null && !ReferenceEquals(j, SelectedJob)).ToArray();
    }
    async void RefreshQuality(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { Pdf: not null } job) return;
        job.Analysis = null; job.AnalysisError = null;
        await InspectAsync(job); RefreshInspection();
    }
    async Task EnsureIndexesAsync()
    {
        await Task.WhenAll(jobs.Where(j => j.Pdf != null).Select(InspectAsync).ToArray());
    }
    async void SearchDocuments(object sender, RoutedEventArgs e)
    {
        try {
            await EnsureIndexesAsync();
            SearchResults.ItemsSource = DocumentIntelligence.Search(jobs, SearchQuery.Text);
            SearchNotice.Text = $"{SearchResults.Items.Count}건 (최대 300건) · OCR 전 스캔 페이지는 검색에서 제외됩니다.";
        } catch (Exception ex) { SearchNotice.Text = ex.Message; }
    }
    async void OpenEvidence(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: Evidence hit }) return;
        Files.SelectedItem = hit.Job;
        SmartToggle.IsChecked = false; SetMenuOpen(false);
        await ShowPreviewAsync(hit.Job);
        if(hit.Page>0 && ReferenceEquals(editorJob,hit.Job) && pdfEditor is { } editor) {
            try {await editor.Ready;await editor.NavigateToPageAsync(hit.Page);}
            catch(OperationCanceledException) {}
            catch(Exception ex) {Summary.Text=ex.Message;}
        }
    }
    async void CompareDocuments(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { Pdf: not null } before || CompareTarget.SelectedItem is not FileJob after) { CompareNotice.Text = "현재 문서와 비교할 문서를 선택하세요."; return; }
        await InspectAsync(before); await InspectAsync(after);
        var results = DocumentIntelligence.Compare(before, after);
        CompareResults.ItemsSource = results;
        CompareNotice.Text = before.Analysis == null || after.Analysis == null ? "문서 분석을 완료하지 못했습니다." : $"같은 페이지 위치의 문장 비교 · {results.Length}건\n스캔·도형·배치 변화는 화면에서 확인하세요.";
    }
    async void ExtractEvidence(object sender, RoutedEventArgs e)
    {
        await EnsureIndexesAsync();
        AnswerResults.ItemsSource = DocumentIntelligence.Excerpts(jobs, Question.Text);
        AnswerText.Text = "문서에서 추출한 근거 문장입니다. 두 번 클릭하면 해당 페이지로 이동합니다.";
    }
    async void RefreshModels(object sender, RoutedEventArgs e)
    {
        try { LocalModels.ItemsSource = await LocalDocumentAi.ModelsAsync(lifetime.Token); if (LocalModels.Items.Count > 0) LocalModels.SelectedIndex = 0; AnswerText.Text = LocalModels.Items.Count > 0 ? "다운로드된 로컬 모델을 선택하세요." : "사용 가능한 로컬 모델이 없습니다."; }
        catch (Exception) { AnswerText.Text = "내장 모델이 포함된 전체 설치판을 사용하거나 이 PC의 Ollama 로컬 모델을 준비해 주세요. 근거 찾기는 AI 없이 사용할 수 있습니다."; }
    }
    async void AskLocalAi(object sender, RoutedEventArgs e)
    {
        if (LocalModels.SelectedItem is not string model) { AnswerText.Text = "로컬 모델을 먼저 불러와 선택하세요."; return; }
        if (!await smartWork.WaitAsync(0)) return;
        try {
            AnswerText.Text = "로컬 AI가 문서 근거를 확인하고 있습니다…";
            await EnsureIndexesAsync();
            var evidence = DocumentIntelligence.Excerpts(jobs, Question.Text);
            var answer = await LocalDocumentAi.AskAsync(model, string.IsNullOrWhiteSpace(Question.Text) ? "문서의 주요 내용을 요약해 주세요." : Question.Text, evidence, lifetime.Token);
            AnswerText.Text = answer.Answer + "\n\nAI 해석은 아래 원문 근거와 함께 확인하세요.";
            AnswerResults.ItemsSource = answer.Evidence;
        } catch (Exception ex) { AnswerText.Text = ex.Message; AnswerResults.ItemsSource = DocumentIntelligence.Excerpts(jobs, Question.Text); }
        finally { smartWork.Release(); }
    }
    async void RunOcr(object sender, RoutedEventArgs e)
    {
        if (SelectedJob is not { Pdf: not null } job) return;
        if (!await smartWork.WaitAsync(0)) return;
        var oldPdf = job.Pdf;
        try {
            QualityNotice.Text = "한국어·영어 OCR 처리 중입니다…";
            var result = await DocumentIntelligence.OcrAsync(oldPdf, lifetime.Token);
            if (!jobs.Contains(job) || job.Pdf != oldPdf) return;
            job.Pdf = result; job.Analysis = null; job.AnalysisError = null;
            await InspectAsync(job);
            if (ReferenceEquals(SelectedJob, job)) await ShowPreviewAsync(job);
            RefreshInspection();
            Summary.Text = "OCR 완료 · 원본은 보존했습니다. 저장 버튼으로 검색 가능한 PDF를 저장하세요.";
        } catch (Exception ex) { QualityNotice.Text = "OCR 실패: " + ex.Message; }
        finally { smartWork.Release(); }
    }
    void PickWatchFolder(object sender, RoutedEventArgs e)
    {
        if (folderAutomation != null) { WatchNotice.Text = "감시를 중지한 뒤 폴더를 변경하세요."; return; }
        var dialog = new OpenFolderDialog { Title = "자동 변환할 새 문서가 들어올 폴더" };
        if (dialog.ShowDialog(this) == true) { watchedFolder = dialog.FolderName; WatchInput.Text = watchedFolder; }
    }
    void PickExportFolder(object sender, RoutedEventArgs e)
    {
        if (folderAutomation != null) { WatchNotice.Text = "감시를 중지한 뒤 폴더를 변경하세요."; return; }
        var dialog = new OpenFolderDialog { Title = "변환된 PDF 저장 폴더 (입력 폴더와 분리)" };
        if (dialog.ShowDialog(this) == true) { exportFolder = dialog.FolderName; WatchOutput.Text = exportFolder; }
    }
    void StartWatching(object sender, RoutedEventArgs e)
    {
        if (watchedFolder == null || exportFolder == null) { WatchNotice.Text = "입력 폴더와 저장 폴더를 선택하세요."; return; }
        if (folderAutomation != null) return;
        try {
            var destination = exportFolder;
            folderAutomation = new(watchedFolder, exportFolder,
                path => Dispatcher.InvokeAsync(() => AutomatedFileAsync(path, destination)).Task.Unwrap(),
                text => Dispatcher.InvokeAsync(() => WatchNotice.Text = text));
            WatchNotice.Text = "감시 중 · 새로 추가·변경되는 문서를 변환합니다. 앱을 닫으면 감시가 종료됩니다.";
        } catch (Exception ex) { WatchNotice.Text = ex.Message; }
    }
    void StopWatching(object sender, RoutedEventArgs e) { folderAutomation?.Dispose(); folderAutomation = null; WatchNotice.Text = "감시 중지 · 이미 진행 중인 작업은 완료합니다."; }
    async Task<bool> AutomatedFileAsync(string path, string destination)
    {
        if (closed || folderAutomation == null || exportFolder == null) return false;
        static async Task<string> Fingerprint(string file) { await using var stream = File.OpenRead(file); return Convert.ToHexString(await SHA256.HashDataAsync(stream)); }
        var sourceHash = await Fingerprint(path);
        var menuWasOpen = MenuToggle.IsChecked == true;
        Add([path]); SetMenuOpen(menuWasOpen);
        var job = jobs.First(j => j.Source.Equals(path, StringComparison.OrdinalIgnoreCase));
        job.Analysis = null; job.AnalysisError = null;
        if (job.Status == "완료" && !path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { job.Pdf = null; job.Status = "대기"; }
        await QueueConversionsAsync([job]);
        if (sourceHash != await Fingerprint(path)) { WatchNotice.Text = "입력 파일이 변경되어 다시 처리할 때까지 저장을 보류합니다."; return false; }
        if (job.Pdf == null) { WatchNotice.Text = $"{job.Name}: {job.Detail}"; return false; }
        var result = await Task.Run(() => PdfExportService.Export([new(job.Source, job.Pdf)], destination));
        WatchNotice.Text = result.Errors.Count > 0 ? string.Join("\n", result.Errors) : $"자동 저장 완료: {job.Name}";
        return result.Saved.Count == 1;
    }
}

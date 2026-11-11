using Microsoft.Win32;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace Topdf;

internal sealed partial class NasApproval : IDisposable
{
    internal static string? ServerUrl => Metadata("TopdfApprovalUrl");
    static string? Metadata(string key) => typeof(NasApproval).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == key)?.Value;

    readonly HttpClient client;
    readonly string token;
    internal sealed record Status(string Code, string State)
    {
        public bool Approved => State == "approved";
    }

    internal NasApproval()
    {
        var url = new Uri(ServerUrl!);
        var pin = Convert.FromHexString(Metadata("TopdfApprovalPin") ?? "");
        if (url.Scheme != Uri.UriSchemeHttps || pin.Length != 32) throw new InvalidOperationException("NAS 승인 서버 설정을 확인하세요.");
        token = LoadToken();
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            certificate != null && DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() &&
            DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), pin);
        client = new HttpClient(handler) { BaseAddress = url, Timeout = TimeSpan.FromSeconds(5) };
    }

    static string LoadToken()
    {
        // The approved identity belongs to this Windows profile. The installer contains no token or administrator password.
        var folder = Path.Combine(AppPaths.DataRoot, "nas-approval");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "device-token.dpapi");
        if (!File.Exists(path))
        {
            var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                var protectedToken=ProtectedData.Protect(RandomNumberGenerator.GetBytes(32), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(temporary,protectedToken);
                try { File.Move(temporary,path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { File.Delete(temporary); }
        }
        var token=ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);
        if(token.Length!=32)throw new InvalidDataException("이 PC의 TOPDF 승인 정보가 손상되었습니다.");
        return Convert.ToHexStringLower(token);
    }

    internal async Task<Status> CheckAsync(CancellationToken cancellation = default)
    {
        try
        {
            var request = System.Text.Json.JsonSerializer.Serialize(
                new { token, pc = Environment.MachineName, username = Environment.UserName });
            using var content = new StringContent(request, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("device/check", content, cancellation);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<ServerStatus>(cancellation);
            if (result?.Status is "pending" or "approved" or "revoked") return new Status(result.Code, result.Status);
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) &&
            !cancellation.IsCancellationRequested) { }
        return new Status("", "unavailable");
    }

    sealed record ServerStatus(string Code, string Status);

    internal bool WaitForApproval(Status status, MainWindow? owner = null)
    {
        using var cancellation = new CancellationTokenSource();
        var window = new Window {
            Title = "TOPDF · 사내 사용 승인", Width = 460, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        if (owner != null) window.Owner = owner;
        var layout = new StackPanel { Margin = new Thickness(20) };
        var statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var code = new TextBox { IsReadOnly = true, FontSize = 22, Margin = new Thickness(0, 0, 0, 14) };
        layout.Children.Add(statusText);
        layout.Children.Add(code);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (owner != null)
        {
            var save = new Button { Content = "편집 파일 저장", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0),
                IsEnabled = owner.ActivePdfEditor != null };
            save.Click += async (_, _) => {
                if (owner.ActivePdfEditor is not {} editor) return;
                var dialog = new SaveFileDialog { Filter = "TOPDF 편집 파일|*.topdf", FileName = "TOPDF-작업.topdf" };
                if (dialog.ShowDialog(window) != true) return;
                save.IsEnabled = false;
                try { await editor.SaveProjectAsync(dialog.FileName); }
                catch (Exception ex) { MessageBox.Show(window, ex.Message, "편집 파일 저장"); }
                finally { save.IsEnabled = true; }
            };
            actions.Children.Add(save);
        }
        var close = new Button { Content = "닫기", IsCancel = true, Padding = new Thickness(12, 5, 12, 5) };
        close.Click += (_, _) => window.DialogResult = false;
        actions.Children.Add(close);
        layout.Children.Add(actions);
        window.Content = layout;
        window.Closed += (_, _) => cancellation.Cancel();
        void Display(Status current)
        {
            statusText.Text = current.State switch {
                "pending" => "승인 대기 중입니다. 아래 코드를 관리자에게 알려 주세요.",
                "revoked" => "이 PC의 권한이 회수됐습니다. 관리자에게 문의해 주세요.",
                _ => "NAS 승인 서버에 연결할 수 없습니다. 사내망에 연결한 뒤 기다려 주세요."
            };
            if (!string.IsNullOrEmpty(current.Code)) code.Text = current.Code;
            code.Visibility = string.IsNullOrEmpty(code.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        Display(status);
        window.Loaded += async (_, _) => {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
                    var next = await CheckAsync(cancellation.Token);
                    if (cancellation.IsCancellationRequested) return;
                    if (next.Approved) { window.DialogResult = true; return; }
                    Display(next);
                }
            }
            catch (OperationCanceledException) { }
        };
        return window.ShowDialog() == true;
    }

    internal async Task MonitorAsync(MainWindow window, CancellationToken cancellation)
    {
        var lastApproval = System.Diagnostics.Stopwatch.StartNew();
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(20), cancellation);
            var status = await CheckAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (status.Approved) { lastApproval.Restart(); continue; }
            if (status.State == "unavailable" && lastApproval.Elapsed < TimeSpan.FromSeconds(60)) continue;
            if (!WaitForApproval(status, window))
            {
                // Let the ordinary close path protect unsaved changes; if it cancels, remain locked.
                window.Close();
                if (!cancellation.IsCancellationRequested)
                {
                    do
                    {
                        if (WaitForApproval(status, window)) { lastApproval.Restart(); break; }
                        window.Close();
                    } while (!cancellation.IsCancellationRequested);
                }
                continue;
            }
            lastApproval.Restart();
        }
    }

    public void Dispose() => client.Dispose();
}

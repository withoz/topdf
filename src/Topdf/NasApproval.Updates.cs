using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;

namespace Topdf;

internal sealed partial class NasApproval
{
    const long MaxInstallerBytes = 1024L * 1024 * 1024;

    sealed record Release(string Version, string Filename, string Sha256, long Size);

    internal void AddUpdateMenu(MainWindow window, MenuItem help)
    {
        var item = new MenuItem { Header = "업데이트 확인" };
        help.Items.Add(item);
        item.Click += async (_, _) =>
        {
            item.IsEnabled = false;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            void Cancel(object? sender, EventArgs args) => cancellation.Cancel();
            window.Closed += Cancel;
            try
            {
                await CheckForUpdateAsync(window, cancellation.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                or OperationCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                if (!cancellation.IsCancellationRequested || window.IsVisible)
                    MessageBox.Show(window, ex.Message, "TOPDF 업데이트");
            }
            finally
            {
                window.Closed -= Cancel;
                item.IsEnabled = true;
            }
        };
    }

    async Task CheckForUpdateAsync(MainWindow window, CancellationToken cancellation)
    {
        using var response = await client.PostAsJsonAsync("device/update", new { token }, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            MessageBox.Show(window, "NAS에 업데이트 서비스가 아직 설치되지 않았습니다.", "TOPDF 업데이트");
            return;
        }
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellation);
        var installed = typeof(NasApproval).Assembly.GetName().Version!;
        var current = new Version(installed.Major, installed.Minor, installed.Build);
        if (release == null)
        {
            MessageBox.Show(window, "배포된 업데이트가 없습니다.", "TOPDF 업데이트");
            return;
        }
        if (!Version.TryParse(release.Version, out var available) || available.Build < 0 ||
            available.Revision >= 0 || release.Filename != $"TOPDF-Setup-{available}-NAS-Approval-win-x64.exe" ||
            release.Sha256 is not { Length: 64 } || !release.Sha256.All(Uri.IsHexDigit) ||
            release.Size is <= 2 or > MaxInstallerBytes)
            throw new IOException("NAS가 반환한 설치 파일 정보가 올바르지 않습니다.");
        if (available <= current)
        {
            MessageBox.Show(window, $"최신 버전을 사용 중입니다. ({current})", "TOPDF 업데이트");
            return;
        }
        if (MessageBox.Show(window, $"{available} 버전을 다운로드하고 설치하시겠습니까? 설치 전에 열린 문서를 저장할 수 있습니다.",
                "TOPDF 업데이트", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        var directory = Path.Combine(AppPaths.DataRoot, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "download.tmp");
        var installer = Path.Combine(directory, release.Filename);
        try
        {
            await DownloadUpdateAsync(release, temporary, cancellation);
            File.Move(temporary, installer);
            cancellation.ThrowIfCancellationRequested();
            var started = false;
            Exception? launchError = null;
            void Launch(object? sender, EventArgs args)
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                    started = process != null;
                }
                catch (System.ComponentModel.Win32Exception ex) { launchError = ex; }
            }
            window.Closed += Launch;
            try { window.Close(); }
            finally { window.Closed -= Launch; }
            if (launchError != null || !started && !window.IsVisible)
                MessageBox.Show($"설치 파일을 직접 실행해 주세요.\n{installer}", "TOPDF 업데이트");
        }
        finally
        {
            File.Delete(temporary);
            if (!File.Exists(installer)) Directory.Delete(directory);
        }
    }

    async Task DownloadUpdateAsync(Release release, string temporary, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "device/download")
        {
            Content = JsonContent.Create(new { token, sha256 = release.Sha256 })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength != release.Size)
            throw new IOException("설치 파일 크기가 일치하지 않습니다.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(cancellation);
        await using var target = File.Create(temporary);
        var buffer = new byte[128 * 1024];
        long written = 0;
        int length;
        while ((length = await source.ReadAsync(buffer, cancellation)) != 0)
        {
            written += length;
            if (written > release.Size) throw new IOException("설치 파일 크기가 일치하지 않습니다.");
            hash.AppendData(buffer, 0, length);
            await target.WriteAsync(buffer.AsMemory(0, length), cancellation);
        }
        if (written != release.Size ||
            !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256)))
            throw new IOException("설치 파일 검증에 실패했습니다.");
    }
}

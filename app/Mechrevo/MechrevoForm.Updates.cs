using System.Diagnostics;
using System.Net.Http;

namespace YALM.Mechrevo;

internal sealed partial class MechrevoForm
{
    private readonly Label _versionLabel = TextLabel(VersionText + " · 检查更新");
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 12 * 60 * 60 * 1000 };
    private bool _updateBusy;
    private static Version CurrentVersion => typeof(MechrevoForm).Assembly.GetName().Version!;
    private static string DisplayVersion(Version version) => version.ToString(version.Revision > 0 ? 4 : 3);
    private static string VersionText => "版本：" + DisplayVersion(CurrentVersion);
    private bool UpdatesEnabled => !_demo && !_startupTest && !_smokeTest;

    private void BuildUpdateLayout(TableLayoutPanel layout)
    {
        _versionLabel.Margin = new Padding(0, 6, 0, 0);
        _versionLabel.Cursor = Cursors.Hand;
        _versionLabel.AccessibleName = "检查程序更新";
        _versionLabel.Enabled = UpdatesEnabled;
        _versionLabel.Click += async (_, _) => await CheckUpdatesAsync(manual: true);
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync(manual: false);
        AddRow(layout, _versionLabel);
    }

    private void StartUpdateChecks()
    {
        if (!UpdatesEnabled) return;
        _updateTimer.Start();
        _ = CheckUpdatesAsync(manual: false);
    }

    private void SetUpdateStatus(string text)
    {
        if (!_closing && !IsDisposed) _versionLabel.Text = text;
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (!UpdatesEnabled || _closing || _updateBusy || (!manual && AppConfig.Is("skip_updates"))) return;
        _updateBusy = true;
        List<(Form Form, bool WasEnabled)>? disabledForms = null;
        try
        {
            SetUpdateStatus("正在检查更新…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var client = new MechrevoUpdateClient(http);
            // A manual click always obtains fresh release metadata, even after an earlier notification.
            MechrevoRelease? release = await client.CheckAsync(CurrentVersion, _lifetime.Token);
            if (_closing) return;
            SetUpdateStatus(release is null ? VersionText + " · 暂无可用更新"
                : $"下载更新：{DisplayVersion(CurrentVersion)} → {DisplayVersion(release.Version)}");
            if (release is null) return;
            if (!manual)
            {
                if (_trayIcon?.Visible == true)
                    _trayIcon.ShowBalloonTip(5000, "YALM 有新版本", "打开主窗口，点击底部版本信息即可更新。", ToolTipIcon.Info);
                return;
            }

            // Development builds have companion files and must be updated by building/publishing.
            // Only portable single-file executables can safely replace themselves.
#pragma warning disable IL3000
            bool portable = string.IsNullOrEmpty(typeof(MechrevoForm).Assembly.Location);
#pragma warning restore IL3000
            if (!portable)
            {
                MessageBox.Show(this, "当前是开发构建，请从发布页面下载便携版。", "程序更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                OpenReleasesPage();
                return;
            }
            if (MessageBox.Show(this, $"发现新版本 {DisplayVersion(release.Version)}，是否下载并重启？\n当前程序会保留备份，用户设置继续保留。",
                "程序更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            SetUpdateStatus("正在下载更新：0%");
            using var downloadHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var downloader = new MechrevoUpdateClient(downloadHttp);
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YALM", "Updates", Guid.NewGuid().ToString("N"));
            var progress = new Progress<int>(percent => SetUpdateStatus($"正在下载更新：{percent}%"));
            PreparedMechrevoUpdate prepared = await downloader.PrepareAsync(release, directory, progress, _lifetime.Token);
            if (_closing) return;
            if (_switching || _keyboardBusy || _customModeForm?.IsOperationPending == true)
                throw new InvalidOperationException("Another operation is pending.");
            SetUpdateStatus("更新已就绪，正在重启…");
            disabledForms = OwnedForms.Prepend(this).Select(form => (form, form.Enabled)).ToList();
            foreach (var (form, _) in disabledForms) form.Enabled = false;
            await MechrevoUpdateInstaller.StartAsync(prepared, Application.ExecutablePath, _lifetime.Token);
            ExitApplication();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error)
        {
            Logger.WriteLine("程序更新失败；" + MechrevoDiagnostics.ExceptionSummary(error));
            SetUpdateStatus(VersionText + " · 更新失败，点击重试");
            if (manual && !_closing)
                MessageBox.Show(this, "更新未完成，当前程序继续运行。请稍后重试，或从发布页面下载。",
                    "程序更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            _updateBusy = false;
            if (!_closing && disabledForms is not null)
                foreach (var (form, wasEnabled) in disabledForms)
                    if (!form.IsDisposed) form.Enabled = wasEnabled;
        }
    }

    private static void OpenReleasesPage()
    {
        try { Process.Start(new ProcessStartInfo(MechrevoRelease.ReleasesUrl) { UseShellExecute = true }); }
        catch (Exception error) { Logger.WriteLine("发布页面打开失败；" + MechrevoDiagnostics.ExceptionSummary(error)); }
    }
}

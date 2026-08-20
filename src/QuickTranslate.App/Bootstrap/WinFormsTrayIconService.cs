using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.Services;
using CoreToolTipIcon = QuickTranslate.Core.Abstractions.ToolTipIcon;
using WFSToolTipIcon = System.Windows.Forms.ToolTipIcon;

namespace QuickTranslate.App.Bootstrap;

public class WinFormsTrayIconService : ITrayIconService, IDisposable
{
    private readonly IAppLifecycle _appLifecycle;
    private readonly ILogger<WinFormsTrayIconService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private ToolStripMenuItem? _pauseMenuItem;
    private ToolStripMenuItem? _debugModeMenuItem;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private bool _disposed;

    public WinFormsTrayIconService(
        IAppLifecycle appLifecycle,
        ILogger<WinFormsTrayIconService> logger,
        IServiceProvider serviceProvider)
    {
        _appLifecycle = appLifecycle;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public void Show()
    {
        EnsureInitialized();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = true;
        }
    }

    public void Hide()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
        }
    }

    public void ShowNotification(string title, string message, CoreToolTipIcon icon = CoreToolTipIcon.Info)
    {
        EnsureInitialized();
        if (_notifyIcon == null) return;

        var wfIcon = icon switch
        {
            CoreToolTipIcon.None => WFSToolTipIcon.None,
            CoreToolTipIcon.Info => WFSToolTipIcon.Info,
            CoreToolTipIcon.Warning => WFSToolTipIcon.Warning,
            CoreToolTipIcon.Error => WFSToolTipIcon.Error,
            _ => WFSToolTipIcon.Info
        };

        _notifyIcon.ShowBalloonTip(3000, title, message, wfIcon);
    }

    private void EnsureInitialized()
    {
        if (_notifyIcon != null) return;

        _contextMenu = new ContextMenuStrip();

        var openSettingsItem = new ToolStripMenuItem("设置...");
        openSettingsItem.Click += OnOpenSettingsClick;
        _contextMenu.Items.Add(openSettingsItem);

        _pauseMenuItem = new ToolStripMenuItem("暂停");
        _pauseMenuItem.CheckOnClick = true;
        _pauseMenuItem.Checked = _appLifecycle.IsPaused;
        UpdatePauseMenuItemText();
        _pauseMenuItem.Click += OnPauseMenuItemClick;
        _contextMenu.Items.Add(_pauseMenuItem);

        _debugModeMenuItem = new ToolStripMenuItem("调试模式");
        _debugModeMenuItem.CheckOnClick = true;
        _debugModeMenuItem.Checked = _serviceProvider.GetService<IOptions<AppSettings>>()?.Value.DebugOverlayMode == true;
        _debugModeMenuItem.ToolTipText = "开启后选中区域显示为实线框，便于排查选词定位；关闭时显示扫描动画";
        _debugModeMenuItem.Click += OnDebugModeClick;
        _contextMenu.Items.Add(_debugModeMenuItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var aboutItem = new ToolStripMenuItem("关于...");
        aboutItem.Click += OnAboutClick;
        _contextMenu.Items.Add(aboutItem);

        var quitItem = new ToolStripMenuItem("退出");
        quitItem.Click += (s, e) =>
        {
            _appLifecycle.Shutdown(0);
        };
        _contextMenu.Items.Add(quitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "QuickTranslate",
            ContextMenuStrip = _contextMenu,
            Visible = false
        };

        _appLifecycle.Paused += OnAppLifecyclePaused;
        _appLifecycle.Resumed += OnAppLifecycleResumed;
    }

    private void OnOpenSettingsClick(object? sender, EventArgs e)
    {
        _logger.LogInformation("OpenSettings menu clicked");
        try
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_settingsWindow == null || !_settingsWindow.IsLoaded)
                {
                    _settingsWindow = SettingsWindow.Create(_serviceProvider);
                    _settingsWindow.Closed += (s, e2) => _settingsWindow = null;
                    _settingsWindow.Show();
                }
                _settingsWindow.Activate();
                _settingsWindow.Focus();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open SettingsWindow");
            ShowNotification("错误", "打开设置失败: " + ex.Message, CoreToolTipIcon.Error);
        }
    }

    private void OnAboutClick(object? sender, EventArgs e)
    {
        _logger.LogInformation("About menu clicked");
        try
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_aboutWindow == null || !_aboutWindow.IsLoaded)
                {
                    var verifier = _serviceProvider.GetService<ModelVersionVerifier>();
                    _aboutWindow = new AboutWindow(verifier);
                    _aboutWindow.Closed += (s, e2) => _aboutWindow = null;
                    _aboutWindow.Show();
                }
                _aboutWindow.Activate();
                _aboutWindow.Focus();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open AboutWindow");
            ShowNotification("错误", "打开关于窗口失败: " + ex.Message, CoreToolTipIcon.Error);
        }
    }

    private void OnPauseMenuItemClick(object? sender, EventArgs e)
    {
        if (_pauseMenuItem == null) return;

        if (_pauseMenuItem.Checked)
        {
            _appLifecycle.Pause();
        }
        else
        {
            _appLifecycle.Resume();
        }
        UpdatePauseMenuItemText();
    }

    private void OnDebugModeClick(object? sender, EventArgs e)
    {
        if (_debugModeMenuItem == null) return;

        try
        {
            var appSettings = _serviceProvider.GetRequiredService<IOptions<AppSettings>>().Value;
            bool enabled = _debugModeMenuItem.Checked;
            appSettings.DebugOverlayMode = enabled;

            // 持久化到 settings.json（同一实例就地更新，无需重启即生效）
            var settingsManager = _serviceProvider.GetService<ISettingsManager>();
            if (settingsManager != null)
            {
                _ = settingsManager.SaveAsync(appSettings);
            }

            _logger.LogInformation("Debug overlay mode {State} via tray menu", enabled ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle debug overlay mode");
        }
    }

    private void OnAppLifecyclePaused(object? sender, EventArgs e)
    {
        if (_pauseMenuItem != null)
        {
            _pauseMenuItem.Checked = true;
            UpdatePauseMenuItemText();
        }
    }

    private void OnAppLifecycleResumed(object? sender, EventArgs e)
    {
        if (_pauseMenuItem != null)
        {
            _pauseMenuItem.Checked = false;
            UpdatePauseMenuItemText();
        }
    }

    private void UpdatePauseMenuItemText()
    {
        if (_pauseMenuItem == null) return;
        _pauseMenuItem.Text = _pauseMenuItem.Checked ? "启用" : "暂停";
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "icons", "QuickTranslate.ico");
            if (File.Exists(iconPath))
            {
                return new Icon(iconPath);
            }
        }
        catch
        {
            // 加载失败时回退到占位图标，托盘仍可正常使用
        }
        return GeneratePlaceholderIcon();
    }

    private static Icon GeneratePlaceholderIcon()
    {
        const int size = 16;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
        graphics.Clear(Color.Transparent);

        using var brush = new SolidBrush(Color.Red);
        graphics.FillRectangle(brush, 1, 1, size - 2, size - 2);

        using var borderPen = new Pen(Color.Maroon, 1);
        graphics.DrawRectangle(borderPen, 1, 1, size - 2, size - 2);

        var hicon = bitmap.GetHicon();
        var result = (Icon)Icon.FromHandle(hicon).Clone();
        try
        {
            UnsafeNativeMethods.DestroyIcon(hicon);
        }
        catch { }
        return result;
    }

    private static class UnsafeNativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr hIcon);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            if (_appLifecycle != null)
            {
                _appLifecycle.Paused -= OnAppLifecyclePaused;
                _appLifecycle.Resumed -= OnAppLifecycleResumed;
            }

            if (_pauseMenuItem != null)
            {
                _pauseMenuItem.Click -= OnPauseMenuItemClick;
                _pauseMenuItem.Dispose();
                _pauseMenuItem = null;
            }

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            if (_contextMenu != null)
            {
                _contextMenu.Dispose();
                _contextMenu = null;
            }

            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    _settingsWindow?.Close();
                    _aboutWindow?.Close();
                });
            }
            catch { }
        }
        _disposed = true;
    }
}

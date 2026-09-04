using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace QuickTranslate.App.ViewModels;

/// <summary>
/// 启动检查状态，对应 Generic.xaml 已有刷。
/// Pending/Checking/Success/Warning/Failed
/// </summary>
public enum StartupCheckStatus
{
    Pending,
    Checking,
    Success,
    Warning,
    Failed
}

public sealed class StartupCheckItem : INotifyPropertyChanged
{
    private string _title;
    private string _detail;
    private StartupCheckStatus _status;
    private string _iconGlyph;

    public StartupCheckItem(string title, string detail, StartupCheckStatus status = StartupCheckStatus.Pending)
    {
        _title = title;
        _detail = detail;
        _status = status;
        _iconGlyph = MapGlyph(status);
    }

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    public string Detail
    {
        get => _detail;
        set { if (_detail != value) { _detail = value; OnPropertyChanged(); } }
    }

    public StartupCheckStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            IconGlyph = MapGlyph(value);
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>图标字形，随状态自动更新。</summary>
    public string IconGlyph
    {
        get => _iconGlyph;
        private set { if (_iconGlyph != value) { _iconGlyph = value; OnPropertyChanged(); } }
    }

    public string StatusText => _status switch
    {
        StartupCheckStatus.Pending => "待检查",
        StartupCheckStatus.Checking => "检查中…",
        StartupCheckStatus.Success => "已就绪",
        StartupCheckStatus.Warning => "注意",
        StartupCheckStatus.Failed => "未通过",
        _ => string.Empty
    };

    private static string MapGlyph(StartupCheckStatus s) => s switch
    {
        StartupCheckStatus.Pending => "◯",
        StartupCheckStatus.Checking => "⟳",
        StartupCheckStatus.Success => "✓",
        StartupCheckStatus.Warning => "⚠",
        StartupCheckStatus.Failed => "✕",
        _ => "◯"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class StartupSplashViewModel : INotifyPropertyChanged
{
    private double _progress;

    public ObservableCollection<StartupCheckItem> Items { get; } = new();

    /// <summary>总体进度 0..1，用于进度条。</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            var v = Math.Clamp(value, 0d, 1d);
            if (Math.Abs(_progress - v) < 0.0001) return;
            _progress = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{(int)Math.Round(Progress * 100)}%";

    public StartupSplashViewModel()
    {
        // 7 项温和检查卡片，精准对应用户关注的：分辨率/缩放/OCR模型/API 等
        Items.Add(new StartupCheckItem("分辨率 · 显示器", "正在检测分辨率与显示器布局", StartupCheckStatus.Pending));
        Items.Add(new StartupCheckItem("缩放 · DPI", "正在检测缩放与 DPI 感知", StartupCheckStatus.Pending));
        Items.Add(new StartupCheckItem("OCR 模型", "正在校验 PP-OCRv6 模型完整性", StartupCheckStatus.Pending));
        Items.Add(new StartupCheckItem("翻译 API", "正在确认翻译服务与密钥配置", StartupCheckStatus.Pending));
        Items.Add(new StartupCheckItem("全局热键", "正在检查 Alt+1 / Alt+2 可用性", StartupCheckStatus.Pending));
        Items.Add(new StartupCheckItem("缓存与词典", "正在整理本地词典与缓存", StartupCheckStatus.Pending));
        Items.Add(new StartupCheckItem("外观与偏好", "正在应用您的外观与偏好设置", StartupCheckStatus.Pending));
    }

    /// <summary>线程安全更新：按索引。</summary>
    public void UpdateItem(int index, StartupCheckStatus status, string? detail = null)
    {
        RunOnUi(() =>
        {
            if (index < 0 || index >= Items.Count) return;
            var item = Items[index];
            item.Status = status;
            if (detail != null) item.Detail = detail;
        });
    }

    /// <summary>线程安全更新：按标题匹配。</summary>
    public void UpdateItem(string title, StartupCheckStatus status, string? detail = null)
    {
        RunOnUi(() =>
        {
            var item = FindByTitle(title);
            if (item == null) return;
            item.Status = status;
            if (detail != null) item.Detail = detail;
        });
    }

    public void SetProgress(double value)
    {
        RunOnUi(() => Progress = value);
    }

    private StartupCheckItem? FindByTitle(string title)
    {
        foreach (var it in Items)
            if (it.Title == title) return it;
        return null;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

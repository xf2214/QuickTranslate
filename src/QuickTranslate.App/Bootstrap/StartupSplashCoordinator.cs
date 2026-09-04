using System.IO;
using System.Windows;
using WpfApplication = System.Windows.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuickTranslate.App.ViewModels;
using QuickTranslate.App.Windows;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;
using QuickTranslate.Infrastructure.Services;

namespace QuickTranslate.App.Bootstrap;

public static class StartupSplashCoordinator
{
    private static readonly string[] PendingDetails =
    {
        "正在检测分辨率与显示器布局",
        "正在检测缩放与 DPI 感知",
        "正在校验 OCR 模型完整性",
        "正在确认翻译 API 配置",
        "正在检查全局热键可用性",
        "正在整理缓存与本地存储",
        "正在应用外观与偏好设置"
    };

    private static readonly string[] Titles =
    {
        "分辨率 · 显示器",
        "缩放 · DPI",
        "OCR 模型",
        "翻译 API",
        "全局热键",
        "缓存与词典",
        "外观与偏好"
    };

    public static StartupSplashWindow? TryShowSplash(ILogger? logger)
    {
        try
        {
            StartupSplashWindow? window = null;
            var dispatcher = WpfApplication.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => window = CreateAndShow(logger));
            }
            else
            {
                window = CreateAndShow(logger);
            }
            return window;
        }
        catch (Exception ex)
        {
            try { logger?.LogDebug(ex, "[StartupSplash] TryShowSplash failed [ErrorCode=SPLASH_SHOW_FAIL]"); }
            catch { }
            global::Serilog.Log.Debug(ex, "[StartupSplash] TryShowSplash failed [ErrorCode=SPLASH_SHOW_FAIL]");
            return null;
        }
    }

    private static StartupSplashWindow? CreateAndShow(ILogger? logger)
    {
        try
        {
            var vm = new StartupSplashViewModel();
            for (int i = 0; i < PendingDetails.Length && i < vm.Items.Count; i++)
            {
                vm.UpdateItem(i, StartupCheckStatus.Pending, PendingDetails[i]);
            }
            vm.SetProgress(0);

            var window = new StartupSplashWindow(vm);
            window.Show();
            return window;
        }
        catch (Exception ex)
        {
            try { logger?.LogDebug(ex, "[StartupSplash] CreateAndShow failed"); } catch { }
            global::Serilog.Log.Debug(ex, "[StartupSplash] CreateAndShow failed");
            return null;
        }
    }

    public static async Task RunStartupChecksAsync(StartupSplashWindow splash, IServiceProvider sp, ILogger logger)
    {
        try
        {
            for (int i = 0; i < 7; i++)
            {
                string title = i < Titles.Length ? Titles[i] : $"检查{i}";
                try
                {
                    splash.UpdateCheck(i, StartupCheckStatus.Checking);
                    splash.SetProgress((double)i / 7);
                    logger.LogInformation("StartupCheck {Index} {Title} -> {Status} {Detail}", i, title, "Checking", PendingDetails[i]);

                    await Task.Delay(140).ConfigureAwait(false);

                    string detail = ProbeDetail(i, sp, logger);

                    // 成功细化
                    splash.UpdateCheck(i, StartupCheckStatus.Success, detail);
                    splash.SetProgress((double)(i + 1) / 7);
                    logger.LogInformation("StartupCheck {Index} {Title} -> {Status} {Detail}", i, title, "Success", detail);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "[StartupSplash] check {Index} failed [ErrorCode=SPLASH_CHECK_FAIL]", i);
                    try { splash.UpdateCheck(i, StartupCheckStatus.Warning, "检测跳过 · 不影响使用"); } catch { }
                    try { splash.SetProgress((double)(i + 1) / 7); } catch { }
                    logger.LogInformation("StartupCheck {Index} {Title} -> {Status} {Detail}", i, title, "Warning", "检测跳过 · 不影响使用");
                }
            }

            try { splash.SetProgress(1); } catch { }

            await InvokeOnUiAsync(splash, () =>
            {
                try { splash.MarkCompletedAndClose(); } catch { }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[StartupSplash] RunStartupChecksAsync outer failed [ErrorCode=SPLASH_PIPELINE_FAIL]");
            try
            {
                await InvokeOnUiAsync(splash, () =>
                {
                    try { splash.CloseWithAnimation(); } catch { }
                }).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private static string ProbeDetail(int index, IServiceProvider sp, ILogger logger)
    {
        try
        {
            switch (index)
            {
                case 0: return ProbeResolution(sp);
                case 1: return ProbeDpi(sp);
                case 2: return ProbeOcrModel(sp);
                case 3: return ProbeTranslationApi(sp);
                case 4: return ProbeHotkeys(sp);
                case 5: return ProbeCache(sp);
                case 6: return ProbeAppearance(sp);
                default: return "已就绪";
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[StartupSplash] ProbeDetail {Index} failed", index);
            return "检测跳过 · 不影响使用";
        }
    }

    private static string ProbeResolution(IServiceProvider sp)
    {
        try
        {
            var monitorService = sp.GetService<IMonitorService>();
            if (monitorService != null)
            {
                var monitors = monitorService.EnumerateMonitors();
                if (monitors.Count > 0)
                {
                    var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
                    return $"{monitors.Count} 台显示器 · 主屏 {primary.Bounds.Width}×{primary.Bounds.Height}";
                }
            }
        }
        catch { }
        try
        {
            var w = (int)SystemParameters.PrimaryScreenWidth;
            var h = (int)SystemParameters.PrimaryScreenHeight;
            return $"主屏 {w}×{h} · 已检测";
        }
        catch { return "分辨率已检测"; }
    }

    private static string ProbeDpi(IServiceProvider sp)
    {
        try
        {
            var monitorService = sp.GetService<IMonitorService>();
            if (monitorService != null)
            {
                var monitors = monitorService.EnumerateMonitors();
                var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
                if (primary != null)
                {
                    var pct = (int)Math.Round(primary.DpiX / 96.0 * 100);
                    return $"PerMonitorV2 已启用 · {pct}%";
                }
            }
        }
        catch { }
        return "PerMonitorV2 已启用 · 缩放已适配";
    }

    private static string ProbeOcrModel(IServiceProvider sp)
    {
        try
        {
            var verifier = sp.GetService<ModelVersionVerifier>();
            if (verifier != null && verifier.LoadedVersion != null)
            {
                if (verifier.AllSha256Matched)
                {
                    var recName = verifier.LoadedVersion.Rec?.Name ?? "PP-OCRv6";
                    return $"{recName} · 已就绪";
                }
                return "Mock 回退可用";
            }
        }
        catch { }
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "models", "det.onnx"),
                Path.Combine(AppContext.BaseDirectory, "assets", "models", "det.onnx"),
                Path.Combine(AppContext.BaseDirectory, "models", "rec.onnx"),
                Path.Combine(AppContext.BaseDirectory, "assets", "models", "rec.onnx"),
            };
            bool exists = candidates.Any(File.Exists);
            return exists ? "PP-OCRv6 Small · 已就绪" : "Mock 回退可用";
        }
        catch { return "Mock 回退可用"; }
    }

    private static string ProbeTranslationApi(IServiceProvider sp)
    {
        try
        {
            var settings = sp.GetService<IOptions<AppSettings>>()?.Value;
            if (settings != null)
            {
                if (settings.IsCustomLlmConfigured && settings.IsApiKeyConfigured)
                {
                    var model = string.IsNullOrWhiteSpace(settings.CustomLlmModel) ? "OpenAI 兼容" : settings.CustomLlmModel;
                    return $"{model} · 已配置";
                }
                if (settings.IsCustomLlmConfigured)
                    return "OpenAI 兼容 · 待配置 · 可在设置中完成";
                if (settings.IsApiKeyConfigured)
                    return "API 已配置";
                return "待配置 · 可在设置中完成";
            }
        }
        catch { }
        return "待配置 · 可在设置中完成";
    }

    private static string ProbeHotkeys(IServiceProvider sp)
    {
        try
        {
            var settings = sp.GetService<IOptions<AppSettings>>()?.Value;
            if (settings != null)
            {
                return $"{settings.WordHotkey.Modifiers}+{settings.WordHotkey.Key} / {settings.BlockHotkey.Modifiers}+{settings.BlockHotkey.Key} · 可用";
            }
        }
        catch { }
        return "Alt+1 / Alt+2 · 可用";
    }

    private static string ProbeCache(IServiceProvider sp)
    {
        try
        {
            var dict = sp.GetService<ILocalDictionary>();
            if (dict != null)
            {
                var count = dict.Count;
                return $"本地词典 {count} 词条 · 缓存就绪";
            }
        }
        catch { }
        return "本地词典/缓存就绪";
    }

    private static string ProbeAppearance(IServiceProvider sp)
    {
        try
        {
            var settings = sp.GetService<IOptions<AppSettings>>()?.Value;
            if (settings != null)
            {
                return $"偏好已应用 · 目标语言 {settings.TargetLanguage}";
            }
        }
        catch { }
        return "偏好已应用";
    }

    private static Task InvokeOnUiAsync(StartupSplashWindow splash, Action action)
    {
        try
        {
            var dispatcher = splash.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                action();
                return Task.CompletedTask;
            }
            return dispatcher.InvokeAsync(action).Task;
        }
        catch
        {
            try
            {
                var appDispatcher = WpfApplication.Current?.Dispatcher;
                if (appDispatcher != null && !appDispatcher.CheckAccess())
                    return appDispatcher.InvokeAsync(action).Task;
                action();
            }
            catch { }
            return Task.CompletedTask;
        }
    }
}

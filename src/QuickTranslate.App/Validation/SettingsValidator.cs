using System.Collections.Generic;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;

namespace QuickTranslate.App.Validation;

public static class SettingsValidator
{
    /// <summary>
    /// 校验自定义大模型（OpenAI 兼容）配置。allowEmpty=true 时（用户尚未选择自定义模型）
    /// 空配置直接通过；allowEmpty=false 时要求 BaseUrl 为合法 http(s) URI 且模型名非空。
    /// </summary>
    public static ValidationResult ValidateCustomLlm(string? baseUrl, string? model, bool allowEmpty)
    {
        var url = (baseUrl ?? string.Empty).Trim();
        var mdl = (model ?? string.Empty).Trim();

        if (url.Length == 0 && mdl.Length == 0)
        {
            return allowEmpty
                ? ValidationResult.Ok()
                : ValidationResult.Fail("已选择自定义大模型，请填写 API 地址（Base URL）和模型名称");
        }

        if (url.Length == 0)
        {
            return ValidationResult.Fail("自定义大模型缺少 API 地址（Base URL），例如 https://api.openai.com/v1");
        }
        if (mdl.Length == 0)
        {
            return ValidationResult.Fail("自定义大模型缺少模型名称，例如 gpt-4o-mini");
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationResult.Fail("自定义大模型 API 地址必须以 http:// 或 https:// 开头，例如 https://api.openai.com/v1");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ValidationResult.Fail($"自定义大模型 API 地址不是合法 URL：{url}");
        }

        return ValidationResult.Ok();
    }

    private static readonly HashSet<KeyboardKey> ForbiddenKeys = new()
    {
        KeyboardKey.None,
        KeyboardKey.ControlKey,
        KeyboardKey.LeftCtrl,
        KeyboardKey.RightCtrl,
        KeyboardKey.Menu,
        KeyboardKey.LeftAlt,
        KeyboardKey.RightAlt,
        KeyboardKey.ShiftKey,
        KeyboardKey.LeftShift,
        KeyboardKey.RightShift,
        KeyboardKey.LWin,
        KeyboardKey.RWin,
    };

    private static readonly HashSet<KeyboardKey> NotRecommendedKeys = new()
    {
        KeyboardKey.Escape,
        KeyboardKey.Tab,
        KeyboardKey.Return,
        KeyboardKey.Enter,
        KeyboardKey.Back,
        KeyboardKey.PrintScreen,
        KeyboardKey.Snapshot,
        KeyboardKey.Pause,
        KeyboardKey.CapsLock,
        KeyboardKey.NumLock,
        KeyboardKey.Scroll,
    };

    private static readonly (HotkeyModifiers Mods, KeyboardKey Key)[] SystemCommonShortcuts = new[]
    {
        (HotkeyModifiers.Ctrl, KeyboardKey.C),
        (HotkeyModifiers.Ctrl, KeyboardKey.V),
        (HotkeyModifiers.Ctrl, KeyboardKey.X),
        (HotkeyModifiers.Ctrl, KeyboardKey.Z),
        (HotkeyModifiers.Ctrl, KeyboardKey.Y),
        (HotkeyModifiers.Ctrl, KeyboardKey.A),
        (HotkeyModifiers.Ctrl, KeyboardKey.S),
        (HotkeyModifiers.Ctrl, KeyboardKey.F),
        (HotkeyModifiers.Ctrl, KeyboardKey.W),
        (HotkeyModifiers.Ctrl, KeyboardKey.R),
        (HotkeyModifiers.Ctrl, KeyboardKey.T),
        (HotkeyModifiers.Ctrl, KeyboardKey.N),
        (HotkeyModifiers.Alt, KeyboardKey.Tab),
        (HotkeyModifiers.Alt, KeyboardKey.F4),
        (HotkeyModifiers.Win, KeyboardKey.D1),
        (HotkeyModifiers.Win, KeyboardKey.D2),
        (HotkeyModifiers.Win, KeyboardKey.E),
        (HotkeyModifiers.Win, KeyboardKey.L),
    };

    public static ValidationResult Validate(
        HotkeyCombo wordHotkey,
        HotkeyCombo blockHotkey,
        IHotkeyBroker hotkeyBroker)
    {
        var wordCheck = CheckHotkeyValid(wordHotkey, "单词");
        if (!wordCheck.IsSuccess) return wordCheck;

        var blockCheck = CheckHotkeyValid(blockHotkey, "区域");
        if (!blockCheck.IsSuccess) return blockCheck;

        if (wordHotkey.Modifiers == blockHotkey.Modifiers && wordHotkey.Key == blockHotkey.Key)
        {
            return ValidationResult.Fail(
                $"Word 与 Block 热键冲突（相同：{FormatHotkey(wordHotkey)}），请为两者设置不同的组合。建议尝试 Ctrl+Alt+1 / Ctrl+Shift+Q / Win+F8");
        }

        if (!hotkeyBroker.Probe(wordHotkey.Modifiers, wordHotkey.Key))
        {
            return ValidationResult.Fail(
                $"{FormatHotkey(wordHotkey)} 已被系统或其他应用占用发生冲突，无法用作单词热键。建议尝试 Ctrl+Alt+1 / Ctrl+Shift+Q / Win+F8");
        }

        if (!hotkeyBroker.Probe(blockHotkey.Modifiers, blockHotkey.Key))
        {
            return ValidationResult.Fail(
                $"{FormatHotkey(blockHotkey)} 已被系统或其他应用占用发生冲突，无法用作区域热键。建议尝试 Ctrl+Alt+2 / Ctrl+Shift+B / Win+F9");
        }

        return ValidationResult.Ok();
    }

    private static ValidationResult CheckHotkeyValid(HotkeyCombo combo, string label)
    {
        if (combo.Key == KeyboardKey.None)
        {
            return ValidationResult.Fail(
                $"{label} 热键无效：请在按住修饰键（Ctrl/Alt/Shift/Win）的同时按下一个具体按键（字母/数字/功能键）");
        }

        if (ForbiddenKeys.Contains(combo.Key))
        {
            return ValidationResult.Fail(
                $"{label} 热键不能单独使用修饰键（Ctrl/Alt/Shift/Win），需要搭配字母、数字或功能键使用");
        }

        if (NotRecommendedKeys.Contains(combo.Key) && combo.Modifiers == HotkeyModifiers.None)
        {
            return ValidationResult.Fail(
                $"{label} 热键不建议使用 {combo.Key} 键（无修饰符），它可能被系统占用。请加上 Ctrl / Alt / Shift / Win 等修饰键");
        }

        // Ctrl+C / Ctrl+V 等常用系统快捷键：仅警告，不强制拦截
        foreach (var sc in SystemCommonShortcuts)
        {
            if (combo.Modifiers == sc.Mods && combo.Key == sc.Key)
            {
                return ValidationResult.Fail(
                    $"{FormatHotkey(combo)} 是系统常用快捷键，用作 {label} 热键会影响正常操作。建议改为 Ctrl+Alt+ 开头或其他组合");
            }
        }

        return ValidationResult.Ok();
    }

    private static string FormatHotkey(HotkeyCombo c)
    {
        var parts = new List<string>();
        if (c.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (c.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (c.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (c.Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(c.Key.ToString());
        return string.Join("+", parts);
    }
}

public class ValidationResult
{
    public bool IsSuccess { get; }
    public string? ErrorMessage { get; }

    private ValidationResult(bool success, string? error)
    {
        IsSuccess = success;
        ErrorMessage = error;
    }

    public static ValidationResult Ok() => new(true, null);
    public static ValidationResult Fail(string error) => new(false, error);
}

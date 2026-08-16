using System.Collections.Generic;
using System.Text;
using System.Windows;
using SWC = System.Windows.Controls;
using SWI = System.Windows.Input;
using QuickTranslate.Core.Abstractions;
using QuickTranslate.Core.Options;

namespace QuickTranslate.App.Windows.Controls;

public enum HotkeyProbeStatus
{
    Unknown,        // 未探测
    Available,      // 可用
    SelfConflict,   // 与 Word/Block 另一个热键相同
    ExternalConflict, // 被外部应用占用
    Invalid         // 无效键位（纯修饰符等）
}

public class HotkeyRecorder : SWC.Control
{
    private SWC.TextBox? _textBox;
    private SWC.Button? _recordButton;
    private SWC.Button? _clearButton;
    private bool _isRecording;
    private HotkeyModifiers _capturedModifiers;
    private KeyboardKey _capturedKey;

    public IHotkeyBroker? Broker { get; set; }

    /// <summary>用户通过录制或清除改变了热键（而非初始化赋值）</summary>
    public event EventHandler<HotkeyCombo>? HotkeyChangedByUser;

    /// <summary>用于自冲突检测：由父窗口设置"对方"的当前热键</summary>
    public HotkeyCombo OtherHotkey
    {
        get => (HotkeyCombo)GetValue(OtherHotkeyProperty);
        set => SetValue(OtherHotkeyProperty, value);
    }

    public static readonly DependencyProperty OtherHotkeyProperty =
        DependencyProperty.Register(
            nameof(OtherHotkey),
            typeof(HotkeyCombo),
            typeof(HotkeyRecorder),
            new FrameworkPropertyMetadata(new HotkeyCombo(), OnOtherHotkeyChanged));

    private static void OnOtherHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyRecorder hr)
        {
            hr.RunProbe();
        }
    }

    public HotkeyProbeStatus ProbeStatus
    {
        get => (HotkeyProbeStatus)GetValue(ProbeStatusProperty);
        set => SetValue(ProbeStatusProperty, value);
    }

    public static readonly DependencyProperty ProbeStatusProperty =
        DependencyProperty.Register(
            nameof(ProbeStatus),
            typeof(HotkeyProbeStatus),
            typeof(HotkeyRecorder),
            new FrameworkPropertyMetadata(HotkeyProbeStatus.Unknown, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string StatusMessage
    {
        get => (string)GetValue(StatusMessageProperty);
        set => SetValue(StatusMessageProperty, value);
    }

    public static readonly DependencyProperty StatusMessageProperty =
        DependencyProperty.Register(
            nameof(StatusMessage),
            typeof(string),
            typeof(HotkeyRecorder),
            new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty HotkeyProperty =
        DependencyProperty.Register(
            nameof(Hotkey),
            typeof(HotkeyCombo),
            typeof(HotkeyRecorder),
            new FrameworkPropertyMetadata(new HotkeyCombo(), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyChanged));

    public HotkeyCombo Hotkey
    {
        get => (HotkeyCombo)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyRecorder hr)
        {
            hr.UpdateDisplay();
            hr.RunProbe();
        }
    }

    static HotkeyRecorder()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(HotkeyRecorder), new FrameworkPropertyMetadata(typeof(HotkeyRecorder)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        _textBox = GetTemplateChild("PART_TextBox") as SWC.TextBox;
        _recordButton = GetTemplateChild("PART_RecordButton") as SWC.Button;
        _clearButton = GetTemplateChild("PART_ClearButton") as SWC.Button;

        if (_textBox != null)
        {
            _textBox.IsReadOnly = true;
            _textBox.PreviewKeyDown += TextBox_PreviewKeyDown;
            // 注意：GotFocus 不再自动进入录制模式——之前的实现会导致 Tab 路过或窗体打开
            // 时焦点落到录制框，录制模式吞掉所有键盘输入（表现为"数字键1无法正常使用"）。
            // 现在只允许用户显式点击"录制"按钮才开始捕获。
            _textBox.GotFocus += (s, e) => _textBox.SelectAll();
        }

        if (_recordButton != null)
        {
            _recordButton.Click += RecordButton_Click;
        }

        if (_clearButton != null)
        {
            _clearButton.Click += ClearButton_Click;
        }

        // 支持录制鼠标侧键（XButton1 / XButton2）——仅在录制模式下生效
        PreviewMouseDown += Control_PreviewMouseDown;

        UpdateDisplay();
        RunProbe();
    }

    private void Control_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        KeyboardKey mouseKey = e.ChangedButton switch
        {
            System.Windows.Input.MouseButton.XButton1 => KeyboardKey.XButton1,
            System.Windows.Input.MouseButton.XButton2 => KeyboardKey.XButton2,
            _ => KeyboardKey.None
        };

        if (mouseKey == KeyboardKey.None)
            return;

        e.Handled = true;

        if (!_isRecording)
            return;

        CommitHotkey(mouseKey);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var cleared = new HotkeyCombo(HotkeyModifiers.None, KeyboardKey.None);
        Hotkey = cleared;
        ProbeStatus = HotkeyProbeStatus.Invalid;
        StatusMessage = "未设置";
        UpdateDisplay();
        HotkeyChangedByUser?.Invoke(this, cleared);
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRecording)
        {
            StartRecording();
            _textBox?.Focus();
        }
        else
        {
            StopRecording();
        }
    }

    private void StartRecording()
    {
        _isRecording = true;
        if (_recordButton != null)
            _recordButton.Content = "停止";
        if (_textBox != null)
            _textBox.Text = "按下热键...";
    }

    private void StopRecording()
    {
        _isRecording = false;
        if (_recordButton != null)
            _recordButton.Content = "录制";
        UpdateDisplay();
    }

    private void TextBox_PreviewKeyDown(object sender, SWI.KeyEventArgs e)
    {
        // 只有录制模式下才吞按键；非录制模式要允许键盘事件正常通过（否则 Tab 切换焦点、
        // 输入框数字/字母全部被吞，表现为"键盘按1没反应"等严重问题）
        if (!_isRecording)
            return;

        e.Handled = true;

        var wpfKey = e.Key == SWI.Key.System ? e.SystemKey : e.Key;

        KeyboardKey key = MapWpfKeyToKeyboardKey(wpfKey);

        if (key == KeyboardKey.None)
            return;

        if (IsModifierKey(key))
        {
            _capturedModifiers = CurrentModifiers();
            _capturedKey = KeyboardKey.None;
            _textBox!.Text = FormatHotkey(_capturedModifiers, KeyboardKey.None);
            return;
        }

        CommitHotkey(key);
    }

    private HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (SWI.Keyboard.Modifiers.HasFlag(SWI.ModifierKeys.Control))
            modifiers |= HotkeyModifiers.Ctrl;
        if (SWI.Keyboard.Modifiers.HasFlag(SWI.ModifierKeys.Alt))
            modifiers |= HotkeyModifiers.Alt;
        if (SWI.Keyboard.Modifiers.HasFlag(SWI.ModifierKeys.Shift))
            modifiers |= HotkeyModifiers.Shift;
        if (SWI.Keyboard.Modifiers.HasFlag(SWI.ModifierKeys.Windows))
            modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }

    private void CommitHotkey(KeyboardKey key)
    {
        _capturedModifiers = CurrentModifiers();
        _capturedKey = key;

        var newCombo = new HotkeyCombo(_capturedModifiers, key);
        Hotkey = newCombo;

        StopRecording();
        HotkeyChangedByUser?.Invoke(this, newCombo);
    }

    /// <summary>
    /// 判断是否为修饰键（含左右侧变体）。WPF 发送的是 LeftCtrl/RightCtrl 等，
    /// 而非通用 ControlKey/ShiftKey/Menu，因此需全部覆盖。
    /// </summary>
    private static bool IsModifierKey(KeyboardKey key)
    {
        return key == KeyboardKey.ControlKey ||
               key == KeyboardKey.ShiftKey ||
               key == KeyboardKey.Menu ||
               key == KeyboardKey.LeftCtrl ||
               key == KeyboardKey.RightCtrl ||
               key == KeyboardKey.LeftShift ||
               key == KeyboardKey.RightShift ||
               key == KeyboardKey.LeftAlt ||
               key == KeyboardKey.RightAlt ||
               key == KeyboardKey.LWin ||
               key == KeyboardKey.RWin;
    }

    private void UpdateDisplay()
    {
        if (_textBox == null || _isRecording)
            return;

        _textBox.Text = FormatHotkey(Hotkey.Modifiers, Hotkey.Key);
    }

    private void RunProbe()
    {
        // 未设置
        if (Hotkey.Key == KeyboardKey.None)
        {
            ProbeStatus = HotkeyProbeStatus.Unknown;
            StatusMessage = string.Empty;
            return;
        }

        // 自冲突：与 OtherHotkey 完全相同
        var other = OtherHotkey;
        if (other.Key != KeyboardKey.None &&
            other.Modifiers == Hotkey.Modifiers &&
            other.Key == Hotkey.Key)
        {
            ProbeStatus = HotkeyProbeStatus.SelfConflict;
            StatusMessage = "与另一热键相同";
            return;
        }

        // 修饰键单独存在
        if (IsModifierKey(Hotkey.Key))
        {
            ProbeStatus = HotkeyProbeStatus.Invalid;
            StatusMessage = "仅修饰键无效";
            return;
        }

        // 外部冲突探测
        if (Broker != null)
        {
            bool ok = Broker.Probe(Hotkey.Modifiers, Hotkey.Key);
            if (ok)
            {
                ProbeStatus = HotkeyProbeStatus.Available;
                StatusMessage = "可用";
            }
            else
            {
                ProbeStatus = HotkeyProbeStatus.ExternalConflict;
                StatusMessage = "已被占用";
            }
        }
        else
        {
            ProbeStatus = HotkeyProbeStatus.Unknown;
            StatusMessage = string.Empty;
        }
    }

    public static string FormatHotkey(HotkeyModifiers mods, KeyboardKey key)
    {
        var sb = new StringBuilder();

        if (mods.HasFlag(HotkeyModifiers.Ctrl))
            sb.Append("Ctrl+");
        if (mods.HasFlag(HotkeyModifiers.Alt))
            sb.Append("Alt+");
        if (mods.HasFlag(HotkeyModifiers.Shift))
            sb.Append("Shift+");
        if (mods.HasFlag(HotkeyModifiers.Win))
            sb.Append("Win+");

        if (key != KeyboardKey.None)
            sb.Append(KeyboardKeyFriendlyName(key));
        else if (sb.Length > 0)
            sb.Length -= 1;

        return sb.ToString();
    }

    public static string KeyboardKeyFriendlyName(KeyboardKey key)
    {
        return key switch
        {
            KeyboardKey.D0 => "0",
            KeyboardKey.D1 => "1",
            KeyboardKey.D2 => "2",
            KeyboardKey.D3 => "3",
            KeyboardKey.D4 => "4",
            KeyboardKey.D5 => "5",
            KeyboardKey.D6 => "6",
            KeyboardKey.D7 => "7",
            KeyboardKey.D8 => "8",
            KeyboardKey.D9 => "9",
            KeyboardKey.Escape => "Esc",
            KeyboardKey.Return => "Enter",
            KeyboardKey.Space => "Space",
            KeyboardKey.PageUp => "PageUp",
            KeyboardKey.PageDown => "PageDown",
            KeyboardKey.NumPad0 => "Num0",
            KeyboardKey.NumPad1 => "Num1",
            KeyboardKey.NumPad2 => "Num2",
            KeyboardKey.NumPad3 => "Num3",
            KeyboardKey.NumPad4 => "Num4",
            KeyboardKey.NumPad5 => "Num5",
            KeyboardKey.NumPad6 => "Num6",
            KeyboardKey.NumPad7 => "Num7",
            KeyboardKey.NumPad8 => "Num8",
            KeyboardKey.NumPad9 => "Num9",
            KeyboardKey.OemSemicolon => ";",
            KeyboardKey.OemPlus => "=",
            KeyboardKey.OemComma => ",",
            KeyboardKey.OemMinus => "-",
            KeyboardKey.OemPeriod => ".",
            KeyboardKey.OemQuestion => "/",
            KeyboardKey.OemTilde => "`",
            KeyboardKey.OemOpenBrackets => "[",
            KeyboardKey.OemPipe => "\\",
            KeyboardKey.OemCloseBrackets => "]",
            KeyboardKey.OemQuotes => "'",
            KeyboardKey.XButton1 => "鼠标侧键1",
            KeyboardKey.XButton2 => "鼠标侧键2",
            KeyboardKey.None => "",
            _ => key.ToString()
        };
    }

    public static KeyboardKey MapWpfKeyToKeyboardKey(SWI.Key wpfKey)
    {
        return wpfKey switch
        {
            SWI.Key.None => KeyboardKey.None,
            SWI.Key.Escape => KeyboardKey.Escape,
            SWI.Key.Tab => KeyboardKey.Tab,
            SWI.Key.Enter => KeyboardKey.Enter,
            SWI.Key.Space => KeyboardKey.Space,
            SWI.Key.Back => KeyboardKey.Back,
            SWI.Key.PageUp => KeyboardKey.PageUp,
            SWI.Key.PageDown => KeyboardKey.PageDown,
            SWI.Key.End => KeyboardKey.End,
            SWI.Key.Home => KeyboardKey.Home,
            SWI.Key.Left => KeyboardKey.Left,
            SWI.Key.Up => KeyboardKey.Up,
            SWI.Key.Right => KeyboardKey.Right,
            SWI.Key.Down => KeyboardKey.Down,
            SWI.Key.Insert => KeyboardKey.Insert,
            SWI.Key.Delete => KeyboardKey.Delete,
            SWI.Key.D0 => KeyboardKey.D0,
            SWI.Key.D1 => KeyboardKey.D1,
            SWI.Key.D2 => KeyboardKey.D2,
            SWI.Key.D3 => KeyboardKey.D3,
            SWI.Key.D4 => KeyboardKey.D4,
            SWI.Key.D5 => KeyboardKey.D5,
            SWI.Key.D6 => KeyboardKey.D6,
            SWI.Key.D7 => KeyboardKey.D7,
            SWI.Key.D8 => KeyboardKey.D8,
            SWI.Key.D9 => KeyboardKey.D9,
            SWI.Key.A => KeyboardKey.A,
            SWI.Key.B => KeyboardKey.B,
            SWI.Key.C => KeyboardKey.C,
            SWI.Key.D => KeyboardKey.D,
            SWI.Key.E => KeyboardKey.E,
            SWI.Key.F => KeyboardKey.F,
            SWI.Key.G => KeyboardKey.G,
            SWI.Key.H => KeyboardKey.H,
            SWI.Key.I => KeyboardKey.I,
            SWI.Key.J => KeyboardKey.J,
            SWI.Key.K => KeyboardKey.K,
            SWI.Key.L => KeyboardKey.L,
            SWI.Key.M => KeyboardKey.M,
            SWI.Key.N => KeyboardKey.N,
            SWI.Key.O => KeyboardKey.O,
            SWI.Key.P => KeyboardKey.P,
            SWI.Key.Q => KeyboardKey.Q,
            SWI.Key.R => KeyboardKey.R,
            SWI.Key.S => KeyboardKey.S,
            SWI.Key.T => KeyboardKey.T,
            SWI.Key.U => KeyboardKey.U,
            SWI.Key.V => KeyboardKey.V,
            SWI.Key.W => KeyboardKey.W,
            SWI.Key.X => KeyboardKey.X,
            SWI.Key.Y => KeyboardKey.Y,
            SWI.Key.Z => KeyboardKey.Z,
            SWI.Key.LWin => KeyboardKey.LWin,
            SWI.Key.RWin => KeyboardKey.RWin,
            SWI.Key.NumPad0 => KeyboardKey.NumPad0,
            SWI.Key.NumPad1 => KeyboardKey.NumPad1,
            SWI.Key.NumPad2 => KeyboardKey.NumPad2,
            SWI.Key.NumPad3 => KeyboardKey.NumPad3,
            SWI.Key.NumPad4 => KeyboardKey.NumPad4,
            SWI.Key.NumPad5 => KeyboardKey.NumPad5,
            SWI.Key.NumPad6 => KeyboardKey.NumPad6,
            SWI.Key.NumPad7 => KeyboardKey.NumPad7,
            SWI.Key.NumPad8 => KeyboardKey.NumPad8,
            SWI.Key.NumPad9 => KeyboardKey.NumPad9,
            SWI.Key.Multiply => KeyboardKey.Multiply,
            SWI.Key.Add => KeyboardKey.Add,
            SWI.Key.Subtract => KeyboardKey.Subtract,
            SWI.Key.Decimal => KeyboardKey.Decimal,
            SWI.Key.Divide => KeyboardKey.Divide,
            SWI.Key.F1 => KeyboardKey.F1,
            SWI.Key.F2 => KeyboardKey.F2,
            SWI.Key.F3 => KeyboardKey.F3,
            SWI.Key.F4 => KeyboardKey.F4,
            SWI.Key.F5 => KeyboardKey.F5,
            SWI.Key.F6 => KeyboardKey.F6,
            SWI.Key.F7 => KeyboardKey.F7,
            SWI.Key.F8 => KeyboardKey.F8,
            SWI.Key.F9 => KeyboardKey.F9,
            SWI.Key.F10 => KeyboardKey.F10,
            SWI.Key.F11 => KeyboardKey.F11,
            SWI.Key.F12 => KeyboardKey.F12,
            SWI.Key.F13 => KeyboardKey.F13,
            SWI.Key.F14 => KeyboardKey.F14,
            SWI.Key.F15 => KeyboardKey.F15,
            SWI.Key.F16 => KeyboardKey.F16,
            SWI.Key.F17 => KeyboardKey.F17,
            SWI.Key.F18 => KeyboardKey.F18,
            SWI.Key.F19 => KeyboardKey.F19,
            SWI.Key.F20 => KeyboardKey.F20,
            SWI.Key.F21 => KeyboardKey.F21,
            SWI.Key.F22 => KeyboardKey.F22,
            SWI.Key.F23 => KeyboardKey.F23,
            SWI.Key.F24 => KeyboardKey.F24,
            SWI.Key.NumLock => KeyboardKey.NumLock,
            SWI.Key.Scroll => KeyboardKey.Scroll,
            SWI.Key.LeftShift => KeyboardKey.LeftShift,
            SWI.Key.RightShift => KeyboardKey.RightShift,
            SWI.Key.LeftCtrl => KeyboardKey.LeftCtrl,
            SWI.Key.RightCtrl => KeyboardKey.RightCtrl,
            SWI.Key.LeftAlt => KeyboardKey.LeftAlt,
            SWI.Key.RightAlt => KeyboardKey.RightAlt,
            SWI.Key.System => KeyboardKey.Menu,
            SWI.Key.Snapshot => KeyboardKey.PrintScreen,
            SWI.Key.Oem1 => KeyboardKey.OemSemicolon,
            SWI.Key.OemPlus => KeyboardKey.OemPlus,
            SWI.Key.OemComma => KeyboardKey.OemComma,
            SWI.Key.OemMinus => KeyboardKey.OemMinus,
            SWI.Key.OemPeriod => KeyboardKey.OemPeriod,
            SWI.Key.Oem2 => KeyboardKey.OemQuestion,
            SWI.Key.Oem3 => KeyboardKey.OemTilde,
            SWI.Key.Oem4 => KeyboardKey.OemOpenBrackets,
            SWI.Key.Oem5 => KeyboardKey.OemPipe,
            SWI.Key.Oem6 => KeyboardKey.OemCloseBrackets,
            SWI.Key.Oem7 => KeyboardKey.OemQuotes,
            SWI.Key.Oem8 => KeyboardKey.Oem8,
            SWI.Key.Oem102 => KeyboardKey.OemBackslash,
            _ => KeyboardKey.None
        };
    }
}

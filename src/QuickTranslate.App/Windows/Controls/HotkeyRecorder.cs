using System.Text;
using System.Windows;
using SWC = System.Windows.Controls;
using SWI = System.Windows.Input;
using QuickTranslate.Core.Options;

namespace QuickTranslate.App.Windows.Controls;

public class HotkeyRecorder : SWC.Control
{
    private SWC.TextBox? _textBox;
    private SWC.Button? _recordButton;
    private bool _isRecording;
    private HotkeyModifiers _capturedModifiers;
    private KeyboardKey _capturedKey;

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

        if (_textBox != null)
        {
            _textBox.PreviewKeyDown += TextBox_PreviewKeyDown;
            _textBox.GotFocus += (s, e) => StartRecording();
            _textBox.LostFocus += (s, e) => StopRecording();
        }

        if (_recordButton != null)
        {
            _recordButton.Click += RecordButton_Click;
        }

        UpdateDisplay();
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
        e.Handled = true;

        if (!_isRecording)
            return;

        var wpfKey = e.Key == SWI.Key.System ? e.SystemKey : e.Key;

        var modifiers = HotkeyModifiers.None;
        if (SWI.Keyboard.Modifiers.HasFlag(SWI.ModifierKeys.Control))
            modifiers |= HotkeyModifiers.Ctrl;
        if (SWI.Keyboard.Modifiers.HasFlag(SWI.ModifierKeys.Alt))
            modifiers |= HotkeyModifiers.Alt;
        if (SWI.Keyboard.Modifiers.HasFlag(SWI.ModifierKeys.Shift))
            modifiers |= HotkeyModifiers.Shift;
        if (SWI.Keyboard.Modifiers.HasFlag(SWI.ModifierKeys.Windows))
            modifiers |= HotkeyModifiers.Win;

        KeyboardKey key = MapWpfKeyToKeyboardKey(wpfKey);

        if (key == KeyboardKey.None)
            return;

        if (key == KeyboardKey.ControlKey || key == KeyboardKey.ShiftKey ||
            key == KeyboardKey.Menu || key == KeyboardKey.LWin || key == KeyboardKey.RWin)
        {
            _capturedModifiers = modifiers;
            _capturedKey = KeyboardKey.None;
            _textBox!.Text = FormatHotkey(modifiers, KeyboardKey.None);
            return;
        }

        _capturedModifiers = modifiers;
        _capturedKey = key;

        Hotkey = new HotkeyCombo(modifiers, key);

        StopRecording();
    }

    private void UpdateDisplay()
    {
        if (_textBox == null || _isRecording)
            return;

        _textBox.Text = FormatHotkey(Hotkey.Modifiers, Hotkey.Key);
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

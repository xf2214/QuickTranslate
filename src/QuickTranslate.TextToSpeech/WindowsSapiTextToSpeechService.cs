using System.Globalization;
using System.Speech.Synthesis;

namespace QuickTranslate.TextToSpeech;

/// <summary>
/// 基于 Windows 内置 SAPI（System.Speech）的朗读实现。离线、零服务依赖。
/// </summary>
public sealed class WindowsSapiTextToSpeechService : ITextToSpeechService, IDisposable
{
    private readonly object _gate = new();
    private readonly SpeechSynthesizer _synthesizer;
    private bool _disposed;

    public WindowsSapiTextToSpeechService()
    {
        _synthesizer = new SpeechSynthesizer();
    }

    public bool IsSupported
    {
        get
        {
            lock (_gate)
            {
                return _synthesizer.GetInstalledVoices().Count > 0;
            }
        }
    }

    public void Speak(string text, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        lock (_gate)
        {
            ThrowIfDisposed();

            // 打断上一次朗读，避免叠加。
            if (_synthesizer.State == SynthesizerState.Speaking)
                _synthesizer.SpeakAsyncCancelAll();

            var voice = SelectVoiceFor(language);
            if (voice != null)
                _synthesizer.SelectVoice(voice);

            _synthesizer.SpeakAsync(text);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _synthesizer.SpeakAsyncCancelAll();
        }
    }

    private string? SelectVoiceFor(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        CultureInfo? culture = null;
        try
        {
            culture = CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }

        foreach (var voice in _synthesizer.GetInstalledVoices())
        {
            var info = voice.VoiceInfo;
            if (info != null && info.Culture.Name.Equals(culture.Name, StringComparison.OrdinalIgnoreCase))
                return info.Name;
        }

        return null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsSapiTextToSpeechService));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _synthesizer.SpeakAsyncCancelAll();
            _synthesizer.Dispose();
        }
    }
}
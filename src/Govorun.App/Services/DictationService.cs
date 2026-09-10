using Govorun.Core.Asr;
using Govorun.Core.Audio;
using Govorun.Core.Injection;
using Govorun.Core.Text;
using Serilog;

namespace Govorun.App.Services;

public enum DictationState
{
    Loading,
    Idle,
    Recording,
    Transcribing,
}

/// <summary>
/// The single state machine of the app: hotkey toggles Idle → Recording → Transcribing
/// (async) → text injection → Idle. The ONNX sessions are created once at startup on a
/// background thread and reused for the process lifetime.
/// </summary>
public sealed class DictationService : IDisposable
{
    private readonly AudioRecorder _recorder = new();
    private readonly object _lock = new();
    private ParakeetEngine? _engine;
    private DictationState _state = DictationState.Loading;

    public DictationState State
    {
        get { lock (_lock) return _state; }
    }

    public event Action<DictationState>? StateChanged;
    /// <summary>Mic RMS level 0..1 while recording.</summary>
    public event Action<float>? LevelChanged;
    /// <summary>Raised with the cleaned text after each successful dictation.</summary>
    public event Action<string>? TextRecognized;
    public event Action<string>? EngineLoadFailed;
    public event Action<string>? RecordingFailed;
    public event Action<string>? SilentAudioDetected;

    public TimeSpan RecordingElapsed => _recorder.Elapsed;
    public Task EngineReady { get; private set; } = Task.CompletedTask;
    public double? LastRtf { get; private set; }

    /// <summary>When set, recognized text is not injected — used by the onboarding wizard.</summary>
    public bool SuppressInjection { get; set; }

    /// <summary>Capture device for dictation; null = system default.</summary>
    public string? MicDeviceId
    {
        get => _recorder.DeviceId;
        set => _recorder.DeviceId = value;
    }

    public DictationService()
    {
        _recorder.LevelChanged += level => LevelChanged?.Invoke(level);
        // Raised from the WASAPI capture thread itself — stopping re-entrantly
        // from within the capture's own callback can throw on some devices, and an
        // exception here would be off the UI thread (unrecoverable), so contain it.
        _recorder.MaxDurationReached += () =>
        {
            try { Toggle(); }
            catch (Exception ex) { Log.Error(ex, "Auto-stop at max duration failed"); }
        };
    }

    public void StartEngineLoad(string modelsDirectory)
    {
        EngineReady = Task.Run(() =>
        {
            try
            {
                var engine = new ParakeetEngine(ModelPaths.Locate(modelsDirectory));
                lock (_lock)
                {
                    _engine = engine;
                    _state = DictationState.Idle;
                }
                StateChanged?.Invoke(DictationState.Idle);
                Log.Information("Engine loaded from {Dir}", modelsDirectory);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Engine load failed");
                EngineLoadFailed?.Invoke(ex.Message);
                throw;
            }
        });
    }

    public ParakeetEngine? Engine
    {
        get { lock (_lock) return _engine; }
    }

    /// <summary>Hotkey handler for toggle mode. Safe to call from any thread.</summary>
    public void Toggle()
    {
        if (State == DictationState.Recording) StopAndTranscribe();
        else StartRecording();
    }

    /// <summary>Starts recording if idle. Safe to call from any thread.</summary>
    public void StartRecording()
    {
        bool started;
        string? failure = null;
        lock (_lock)
        {
            if (_state != DictationState.Idle) return;
            try
            {
                _recorder.Start();
                _state = DictationState.Recording;
                started = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start recording");
                failure = ex.Message;
                started = false;
            }
        }
        if (started) StateChanged?.Invoke(DictationState.Recording);
        else if (failure is not null) RecordingFailed?.Invoke(failure);
    }

    /// <summary>
    /// Stops recording and transcribes. Recordings shorter than ~0.4 s
    /// (an accidental push-to-talk tap) are discarded silently.
    /// </summary>
    public void StopAndTranscribe()
    {
        float[] samples;
        lock (_lock)
        {
            if (_state != DictationState.Recording) return;
            samples = _recorder.Stop();
            if (samples.Length < ParakeetEngine.SampleRate * 2 / 5)
            {
                _state = DictationState.Idle;
                samples = Array.Empty<float>();
            }
            else
            {
                _state = DictationState.Transcribing;
            }
        }
        if (samples.Length == 0)
        {
            StateChanged?.Invoke(DictationState.Idle);
            return;
        }
        StateChanged?.Invoke(DictationState.Transcribing);
        Task.Run(() => Transcribe(samples));
    }

    /// <summary>Cancels an in-progress recording without transcribing.</summary>
    public void CancelRecording()
    {
        lock (_lock)
        {
            if (_state != DictationState.Recording) return;
            _recorder.Stop();
            _state = DictationState.Idle;
        }
        StateChanged?.Invoke(DictationState.Idle);
    }

    private void Transcribe(float[] samples)
    {
        try
        {
            var engine = Engine;
            if (engine is null || samples.Length == 0) return;

            // Check if audio is completely silent
            double sumSquares = 0;
            foreach (var sample in samples)
            {
                sumSquares += sample * sample;
            }
            double rms = Math.Sqrt(sumSquares / samples.Length);

            if (rms < 0.0001)
            {
                Log.Warning("Recorded audio is completely silent (RMS {Rms:F6}).", rms);
                SilentAudioDetected?.Invoke("Микрофон ничего не слышит (тишина). Пожалуйста, проверьте настройки микрофона или уровень громкости.");
            }

            var result = engine.Transcribe(samples);
            LastRtf = result.Rtf;
            var text = TextCleaner.Clean(result.Text);
            Log.Information("Transcribed {Seconds:F1}s in {Elapsed:F2}s (RTF {Rtf:F1}): {Length} chars",
                result.AudioSeconds, result.ElapsedSeconds, result.Rtf, text.Length);

            if (text.Length > 0)
            {
                if (!SuppressInjection)
                    TextInjector.Inject(text);
                TextRecognized?.Invoke(text);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Transcription failed");
        }
        finally
        {
            lock (_lock)
            {
                if (_state == DictationState.Transcribing)
                    _state = DictationState.Idle;
            }
            StateChanged?.Invoke(DictationState.Idle);
        }
    }

    public void Dispose()
    {
        _recorder.Dispose();
        lock (_lock) _engine?.Dispose();
    }
}

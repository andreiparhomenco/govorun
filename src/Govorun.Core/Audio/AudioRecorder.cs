using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;

namespace Govorun.Core.Audio;

/// <summary>A capture device visible to the user.</summary>
public sealed record MicDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Captures microphone audio via WASAPI (shared mode) and delivers 16 kHz mono
/// float32 suitable for the ASR engine. Supports whatever sample format the
/// device mix format uses (float32 or PCM 16/24/32). Publishes an RMS level
/// event that feeds the bubble oscillograph and the onboarding mic check.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    public const int TargetSampleRate = 16_000;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(5);
    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly object _lock = new();
    private WasapiCapture? _capture;
    // Held for the capture's lifetime: WasapiCapture keeps using the endpoint, so the
    // COM object can only be released in Stop(), alongside the capture itself.
    private MMDevice? _captureDevice;
    private List<float> _buffer = new();
    private int _sourceRate;
    private int _sourceChannels;
    private int _bytesPerSample;
    private bool _isFloat;

    /// <summary>Device to capture from; null = system default.</summary>
    public string? DeviceId { get; set; }

    /// <summary>RMS level of the last captured buffer, 0..1.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Raised when the recording hits the 5-minute safety cap.</summary>
    public event Action? MaxDurationReached;

    public bool IsRecording { get; private set; }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock) return TimeSpan.FromSeconds((double)_buffer.Count / Math.Max(1, _sourceRate));
        }
    }

    /// <summary>All active capture devices, default first.</summary>
    public static List<MicDevice> ListDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        try
        {
            using var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            defaultId = def.ID;
        }
        catch
        {
            // no default capture device at all
        }

        var result = new List<MicDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
                result.Add(new MicDevice(device.ID, device.FriendlyName, device.ID == defaultId));
        }
        return result.OrderByDescending(d => d.IsDefault).ToList();
    }

    public void Start()
    {
        lock (_lock)
        {
            if (IsRecording) return;
            var device = GetDevice(DeviceId);
            WasapiCapture? capture = null;
            try
            {
                capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);

                var wf = capture.WaveFormat;
                _sourceRate = wf.SampleRate;
                _sourceChannels = wf.Channels;
                _bytesPerSample = wf.BitsPerSample / 8;
                _isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
                           || (wf is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
                Log.Information("Mic capture: '{Device}' {Rate} Hz, {Ch} ch, {Bits}-bit {Kind}",
                    device.FriendlyName, _sourceRate, _sourceChannels, wf.BitsPerSample, _isFloat ? "float" : "PCM");

                _buffer = new List<float>(_sourceRate * 30);
                capture.DataAvailable += OnDataAvailable;
                capture.StartRecording();
                // Only publish the capture once it's fully live — otherwise a stray
                // DataAvailable callback from a half-initialized capture could race
                // the fields above (see OnDataAvailable's ReferenceEquals guard).
                _capture = capture;
                _captureDevice = device;
                IsRecording = true;
            }
            catch
            {
                capture?.Dispose();
                device.Dispose();
                throw;
            }
        }
    }

    /// <summary>Stops the capture and returns the recording as 16 kHz mono float32.</summary>
    public float[] Stop()
    {
        WasapiCapture? capture;
        MMDevice? device;
        List<float> monoAtSourceRate;
        int sourceRate;
        lock (_lock)
        {
            if (!IsRecording) return Array.Empty<float>();
            IsRecording = false;
            capture = _capture;
            _capture = null;
            device = _captureDevice;
            _captureDevice = null;
            monoAtSourceRate = _buffer;
            _buffer = new List<float>();
            sourceRate = _sourceRate;
        }

        if (capture is not null)
        {
            try
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                // Some devices (e.g. a BT headset switching audio profiles) throw
                // here; the recording so far is still valid and must not be lost.
                Log.Warning(ex, "Error stopping capture device");
            }
            finally
            {
                try { capture.Dispose(); }
                catch (Exception ex) { Log.Warning(ex, "Error disposing capture device"); }
            }
        }

        // Released after the capture, which used this endpoint until StopRecording.
        try { device?.Dispose(); }
        catch (Exception ex) { Log.Warning(ex, "Error releasing capture endpoint"); }

        return Resample(monoAtSourceRate, sourceRate, TargetSampleRate);
    }

    private static MMDevice GetDevice(string? deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            if (deviceId is not null)
            {
                try
                {
                    var device = enumerator.GetDevice(deviceId);
                    if (device.State == DeviceState.Active) return device;
                    device.Dispose();
                }
                catch
                {
                    Log.Warning("Saved mic {Id} unavailable, falling back to default", deviceId);
                }
            }
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Defensive: a callback queued by a capture instance that Stop() has since
        // replaced/disposed must never touch the new instance's buffer/format
        // fields — that race was crashing the whole process on a second recording
        // (mismatched frame size reading past the end of the native buffer).
        try
        {
            double sumSquares = 0;
            int frames = 0;
            bool overflow = false;
            lock (_lock)
            {
                if (!IsRecording || !ReferenceEquals(sender, _capture)) return;

                int frameSize = _bytesPerSample * _sourceChannels;
                if (frameSize <= 0) return;
                frames = Math.Min(e.BytesRecorded / frameSize, e.Buffer.Length / frameSize);
                for (int f = 0; f < frames; f++)
                {
                    float mono = 0;
                    for (int c = 0; c < _sourceChannels; c++)
                        mono += ReadSample(e.Buffer, f * frameSize + c * _bytesPerSample);
                    mono /= _sourceChannels;
                    _buffer.Add(mono);
                    sumSquares += mono * mono;
                }
                if (_buffer.Count > (long)_sourceRate * (long)MaxDuration.TotalSeconds)
                    overflow = true;
            }

            if (frames > 0) LevelChanged?.Invoke((float)Math.Sqrt(sumSquares / frames));
            if (overflow) MaxDurationReached?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Dropped a corrupt audio buffer");
        }
    }

    private float ReadSample(byte[] buffer, int offset)
    {
        if (_isFloat)
            return BitConverter.ToSingle(buffer, offset);
        return _bytesPerSample switch
        {
            2 => BitConverter.ToInt16(buffer, offset) / 32768f,
            3 => (buffer[offset] | (buffer[offset + 1] << 8) | ((sbyte)buffer[offset + 2] << 16)) / 8388608f,
            4 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => 0f,
        };
    }

    /// <summary>Linear-interpolation resampler; good enough for speech into a 16 kHz model.</summary>
    public static float[] Resample(List<float> input, int fromRate, int toRate)
    {
        if (input.Count == 0) return Array.Empty<float>();
        if (fromRate == toRate) return input.ToArray();

        double ratio = (double)fromRate / toRate;
        int outLen = (int)(input.Count / ratio);
        var output = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double pos = i * ratio;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, input.Count - 1);
            double frac = pos - i0;
            output[i] = (float)(input[i0] * (1 - frac) + input[i1] * frac);
        }
        return output;
    }

    public void Dispose()
    {
        try { Stop(); } catch { /* device may already be gone */ }
    }
}

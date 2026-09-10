using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Govorun.App.Overlay;

/// <summary>
/// The recording overlay: a dark capsule centered at the bottom of the screen
/// (Wispr Flow style) with a live equalizer of rounded bars in the logo's blues,
/// the parrot logo on the left and a timer on the right.
/// </summary>
public partial class BubbleWindow : Window
{
    private const int BarCount = 15;
    private const double BarWidth = 2;
    private const double BarGap = 2.5;
    private const double MinBarHeight = 2;
    private const double MaxBarHeight = 13;

    private readonly Rectangle[] _bars = new Rectangle[BarCount];
    private readonly double[] _targets = new double[BarCount];
    private readonly double[] _heights = new double[BarCount];
    private readonly Random _rng = new();
    private readonly DispatcherTimer _timer;
    private Func<TimeSpan>? _elapsedProvider;
    // Written from the WASAPI capture thread, read by the render timer on the UI
    // thread. `volatile float` is not legal in C#, so the level rides across as
    // scaled integer ticks.
    private int _levelTicks;
    private bool _transcribing;

    private const int LevelScale = 10_000;

    public BubbleWindow()
    {
        InitializeComponent();

        for (int i = 0; i < BarCount; i++)
        {
            // Gradient across the strip: light sky blue → deep logo blue.
            double t = (double)i / (BarCount - 1);
            var color = Lerp(Color.FromRgb(0x5A, 0xC8, 0xFF), Color.FromRgb(0x1F, 0x5A, 0xD8), t);
            _bars[i] = new Rectangle
            {
                Width = BarWidth,
                Height = MinBarHeight,
                RadiusX = BarWidth / 2,
                RadiusY = BarWidth / 2,
                Fill = new SolidColorBrush(color),
            };
            Canvas.SetLeft(_bars[i], i * (BarWidth + BarGap));
            EqCanvas.Children.Add(_bars[i]);
            _heights[i] = MinBarHeight;
        }
        EqCanvas.Width = BarCount * (BarWidth + BarGap) - BarGap;

        // ~30 fps animation loop: bars chase their targets for a springy feel.
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Animate();
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public void ShowRecording(Func<TimeSpan> elapsedProvider)
    {
        _elapsedProvider = elapsedProvider;
        _transcribing = false;
        Volatile.Write(ref _levelTicks, 0);
        TimerText.Text = "0:00";
        LogoImage.Opacity = 1;
        LogoImage.BeginAnimation(OpacityProperty, null);
        PositionAtBottomCenter();
        Show();
        _timer.Start();
    }

    public void ShowTranscribing()
    {
        _transcribing = true;
        // Pulse the logo while the model works.
        var pulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(450))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        LogoImage.BeginAnimation(OpacityProperty, pulse);
    }

    public void HideBubble()
    {
        _timer.Stop();
        LogoImage.BeginAnimation(OpacityProperty, null);
        Hide();
    }

    /// <summary>Called from the recorder thread with the current RMS level.</summary>
    public void PushLevel(float level) =>
        Volatile.Write(ref _levelTicks, (int)(Math.Clamp(level, 0f, 1f) * LevelScale));

    private void Animate()
    {
        if (!_transcribing && _elapsedProvider is not null)
        {
            var t = _elapsedProvider();
            TimerText.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        // Normalized loudness with a soft knee; each bar gets its own jitter so the
        // strip dances instead of moving as one block.
        float level = Volatile.Read(ref _levelTicks) / (float)LevelScale;
        double loud = _transcribing ? 0 : Math.Min(1.0, Math.Sqrt(level * 14));
        for (int i = 0; i < BarCount; i++)
        {
            // Center bars react more than the edges (bell shape).
            double bell = 0.35 + 0.65 * Math.Exp(-Math.Pow((i - (BarCount - 1) / 2.0) / (BarCount / 3.2), 2));
            double jitter = 0.55 + 0.45 * _rng.NextDouble();
            _targets[i] = MinBarHeight + (MaxBarHeight - MinBarHeight) * loud * bell * jitter;

            // Fast attack, slower decay.
            double k = _targets[i] > _heights[i] ? 0.55 : 0.25;
            _heights[i] += (_targets[i] - _heights[i]) * k;
            _bars[i].Height = _heights[i];
            Canvas.SetTop(_bars[i], (EqCanvas.Height - _heights[i]) / 2);
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;

    /// <summary>
    /// Makes the overlay click-through at the OS level. IsHitTestVisible="False" only
    /// stops hit testing inside the WPF tree — the HWND still receives mouse messages
    /// and swallowed clicks aimed at whatever sits under the capsule.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        int style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void PositionAtBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 36;
    }
}

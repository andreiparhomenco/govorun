using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Govorun.App.Services;
using Govorun.Core.Audio;
using Govorun.Core.Benchmark;
using Govorun.Core.Hotkeys;
using Govorun.Core.Settings;

namespace Govorun.App.Onboarding;

/// <summary>
/// First-run wizard in the Govorun brand style: mic check → hidden RTF benchmark →
/// controls (hotkey + activation mode) → try it live in a demo textbox.
/// </summary>
public partial class WizardWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DictationService _dictation;
    private readonly HotkeyManager _hotkeys;
    private AudioRecorder? _micCheckRecorder;
    private DateTime _voiceSince = DateTime.MaxValue;
    private int _step;
    private readonly Ellipse[] _dots = new Ellipse[4];

    public WizardWindow(AppSettings settings, DictationService dictation, HotkeyManager hotkeys)
    {
        InitializeComponent();
        _settings = settings;
        _dictation = dictation;
        _hotkeys = hotkeys;
        _dictation.TextRecognized += OnTextRecognized;
        Closed += (_, _) =>
        {
            _dictation.TextRecognized -= OnTextRecognized;
            StopMicCheck();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        for (int i = 0; i < _dots.Length; i++)
        {
            _dots[i] = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 7, 0) };
            StepDots.Children.Add(_dots[i]);
        }

        SyncControlsFromSettings();
        EnterStep(0);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void EnterStep(int step)
    {
        _step = step;
        StepMic.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepBench.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepHotkey.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepMagic.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _dots.Length; i++)
            _dots[i].Fill = new SolidColorBrush(i <= step
                ? Color.FromRgb(0x3A, 0x7A, 0xFE)
                : Color.FromRgb(0xD5, 0xDF, 0xF3));

        switch (step)
        {
            case 0:
                StepTitle.Text = "Шаг 1 из 4 — микрофон";
                NextButton.IsEnabled = true; // never trap the user on this step
                PopulateMicCombo();
                StartMicCheck();
                break;
            case 1:
                StepTitle.Text = "Шаг 2 из 4 — оптимизация";
                NextButton.IsEnabled = false;
                _ = RunBenchmarkAsync();
                break;
            case 2:
                StepTitle.Text = "Шаг 3 из 4 — управление";
                NextButton.IsEnabled = true;
                UpdateControlsHint();
                break;
            case 3:
                StepTitle.Text = "Шаг 4 из 4 — попробуйте!";
                MagicInstruction.Text = BuildTryInstruction();
                NextButton.Content = "Готово";
                NextButton.IsEnabled = true;
                MagicBox.Focus();
                break;
        }
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (_step < 3)
        {
            if (_step == 0) StopMicCheck();
            EnterStep(_step + 1);
        }
        else
        {
            _settings.OnboardingCompleted = true;
            _settings.Save();
            App.TrySetAutoStart(_settings.AutoStart);
            Close();
        }
    }

    // --- Step 1: mic check ---

    private bool _suppressComboEvents;
    private DateTime _micCheckStarted;
    private List<MicDevice> _micDevices = new();

    private void PopulateMicCombo()
    {
        _suppressComboEvents = true;
        MicCombo.Items.Clear();
        var devices = AudioRecorder.ListDevices();
        int selected = 0;
        for (int i = 0; i < devices.Count; i++)
        {
            MicCombo.Items.Add(devices[i].Name + (devices[i].IsDefault ? " (по умолчанию)" : ""));
            if (devices[i].Id == _settings.MicDeviceId) selected = i;
        }
        _micDevices = devices;
        if (devices.Count > 0) MicCombo.SelectedIndex = selected;
        _suppressComboEvents = false;
    }

    private void OnMicDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents || MicCombo.SelectedIndex < 0) return;
        _settings.MicDeviceId = _micDevices[MicCombo.SelectedIndex].Id;
        _settings.Save();
        _dictation.MicDeviceId = _settings.MicDeviceId;
        StopMicCheck();
        StartMicCheck();
    }

    private void StartMicCheck()
    {
        try
        {
            _voiceSince = DateTime.MaxValue;
            _micCheckStarted = DateTime.UtcNow;
            MicStatus.Text = "Слушаем микрофон…";
            _micCheckRecorder = new AudioRecorder { DeviceId = _settings.MicDeviceId };
            _micCheckRecorder.LevelChanged += OnMicLevel;
            _micCheckRecorder.Start();
        }
        catch (Exception ex)
        {
            MicStatus.Text = $"Микрофон недоступен: {ex.Message}. Выберите другой в списке выше.";
        }
    }

    private void OnMicLevel(float level)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_step != 0) return;
            MicLevel.Value = Math.Min(1, level * 12);
            if (level > 0.01f)
            {
                if (_voiceSince == DateTime.MaxValue) _voiceSince = DateTime.UtcNow;
                if (DateTime.UtcNow - _voiceSince > TimeSpan.FromSeconds(1.2))
                {
                    MicStatus.Text = "Отлично, микрофон работает!";
                    StopMicCheck();
                    EnterStep(1);
                }
            }
            else
            {
                _voiceSince = DateTime.MaxValue;
                if (DateTime.UtcNow - _micCheckStarted > TimeSpan.FromSeconds(6))
                    MicStatus.Text = "Пока тихо. Если говорите, а шкала молчит — выберите другой микрофон в списке выше.";
            }
        });
    }

    private void StopMicCheck()
    {
        if (_micCheckRecorder is null) return;
        _micCheckRecorder.LevelChanged -= OnMicLevel;
        _micCheckRecorder.Dispose();
        _micCheckRecorder = null;
    }

    // --- Step 2: benchmark ---

    private async Task RunBenchmarkAsync()
    {
        try
        {
            await _dictation.EngineReady;
            var result = await Task.Run(() => RtfBenchmark.Run(_dictation.Engine!));
            _settings.BenchmarkRtf = Math.Round(result.Rtf, 1);
            _settings.Save();
            BenchProgress.IsIndeterminate = false;
            BenchProgress.Value = BenchProgress.Maximum;
            BenchStatus.Text = $"Govorun оптимизирован под ваш процессор.\nМинута речи расшифровывается примерно за {60 / Math.Max(1, result.Rtf):F0} сек (RTF ≈ {result.Rtf:F1}).";
        }
        catch (Exception ex)
        {
            BenchStatus.Text = $"Не удалось выполнить замер: {ex.Message}";
        }
        NextButton.IsEnabled = true;
    }

    // --- Step 3: controls ---

    private void SyncControlsFromSettings()
    {
        HotkeyCtrlWin.IsChecked = _settings.Hotkey == HotkeyMode.CtrlWin;
        HotkeyCtrlSpace.IsChecked = _settings.Hotkey == HotkeyMode.CtrlSpace;
        HotkeyDoubleCtrl.IsChecked = _settings.Hotkey == HotkeyMode.DoubleCtrl;
        if (HotkeyCtrlWin.IsChecked != true && HotkeyCtrlSpace.IsChecked != true && HotkeyDoubleCtrl.IsChecked != true)
            HotkeyCtrlWin.IsChecked = true;
        ModeToggle.IsChecked = _settings.Activation == ActivationMode.Toggle;
        ModeHold.IsChecked = _settings.Activation == ActivationMode.PushToTalk;
    }

    private void OnControlChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.Hotkey = HotkeyCtrlSpace.IsChecked == true ? HotkeyMode.CtrlSpace
                         : HotkeyDoubleCtrl.IsChecked == true ? HotkeyMode.DoubleCtrl
                         : HotkeyMode.CtrlWin;
        _settings.Activation = ModeHold.IsChecked == true ? ActivationMode.PushToTalk : ActivationMode.Toggle;
        _settings.Save();
        _hotkeys.Mode = _settings.Hotkey;
        _hotkeys.Activation = _settings.Activation;
        UpdateControlsHint();
    }

    private string HotkeyName => _settings.Hotkey switch
    {
        HotkeyMode.CtrlSpace => "Ctrl+Space",
        HotkeyMode.DoubleCtrl => "двойной Ctrl",
        _ => "Ctrl+Win",
    };

    private void UpdateControlsHint()
    {
        ControlsHint.Text = _settings.Activation == ActivationMode.PushToTalk
            ? $"Зажмите {HotkeyName} и говорите — отпустите, и текст появится в активном поле."
            : $"Нажмите {HotkeyName}, продиктуйте, нажмите ещё раз — текст появится в активном поле.";
    }

    private string BuildTryInstruction() =>
        (_settings.Activation == ActivationMode.PushToTalk
            ? $"Кликните в поле ниже, зажмите {HotkeyName} и скажите: «Привет, Говорун». Отпустите клавиши."
            : $"Кликните в поле ниже, нажмите {HotkeyName} и скажите: «Привет, Говорун». Затем нажмите {HotkeyName} ещё раз.");

    private void OnTextRecognized(string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_step == 3)
                MagicStatus.Text = "Готово! Теперь диктуйте в любом окне — VS Code, браузер, письмо. " +
                                   "Текст появится там, где стоит курсор.";
        });
    }
}

using System.IO;
using System.Threading;
using System.Windows;
using Govorun.App.History;
using Govorun.App.Onboarding;
using Govorun.App.Overlay;
using Govorun.App.Services;
using Govorun.App.Tray;
using Govorun.Core.Asr;
using Govorun.Core.History;
using Govorun.Core.Hotkeys;
using Govorun.Core.Settings;
using Serilog;

namespace Govorun.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private AppSettings _settings = new();
    private DictationService? _dictation;
    private HotkeyManager? _hotkeys;
    private TrayController? _tray;
    private BubbleWindow? _bubble;
    private HistoryStore? _historyStore;
    private HistoryWindow? _historyWindow;
    private System.Windows.Threading.DispatcherTimer? _watchdog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, @"Global\Govorun.SingleInstance", out bool isNew);
        _ownsMutex = isNew;
        if (!isNew)
        {
            Shutdown();
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(AppSettings.LogPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        Log.Information("Govorun starting, pid {Pid}", Environment.ProcessId);
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled dispatcher exception");
            args.Handled = true;
        };
        // Exceptions off the UI thread (e.g. the WASAPI capture callback) can't be
        // recovered — .NET terminates the process either way — but without this the
        // app would just vanish with nothing in log.txt to explain why.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception, terminating: {Terminating}", args.IsTerminating);
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warning(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _settings = AppSettings.Load();
        _historyStore = new HistoryStore(AppSettings.HistoryPath);

        _dictation = new DictationService();
        _dictation.MicDeviceId = _settings.MicDeviceId;
        _dictation.StartEngineLoad(ModelPaths.DefaultDirectory);

        _historyWindow = new HistoryWindow(_historyStore, _settings);

        _bubble = new BubbleWindow();
        _tray = new TrayController(_settings.AutoStart, _settings.MicDeviceId, _settings.Hotkey, _settings.Activation);
        _tray.SetState(DictationState.Loading);

        _hotkeys = new HotkeyManager { Mode = _settings.Hotkey, Activation = _settings.Activation };
        _hotkeys.Pressed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_hotkeys!.Activation == ActivationMode.PushToTalk)
                _dictation!.StartRecording();
            else
                _dictation!.Toggle();
        });
        _hotkeys.Released += () => Dispatcher.BeginInvoke(() => _dictation!.StopAndTranscribe());

        WireUp();

        // Hourly memory watchdog for long background sessions.
        _watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _watchdog.Tick += (_, _) =>
            Log.Information("Watchdog: working set {Mb} MB", Environment.WorkingSet / (1024 * 1024));
        _watchdog.Start();

        if (!_settings.OnboardingCompleted)
        {
            var wizard = new WizardWindow(_settings, _dictation, _hotkeys);
            wizard.Show();
        }
    }

    private void WireUp()
    {
        var dictation = _dictation!;
        var tray = _tray!;
        var bubble = _bubble!;

        dictation.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            tray.SetState(state);
            switch (state)
            {
                case DictationState.Recording:
                    bubble.ShowRecording(() => dictation.RecordingElapsed);
                    break;
                case DictationState.Transcribing:
                    bubble.ShowTranscribing();
                    break;
                default:
                    bubble.HideBubble();
                    break;
            }
        });

        dictation.LevelChanged += level => bubble.PushLevel(level);
        dictation.TextRecognized += text => _historyStore!.Add(text);
        dictation.SilentAudioDetected += message => Dispatcher.BeginInvoke(() =>
            tray.ShowNotification("Внимание", message));
        dictation.EngineLoadFailed += message => Dispatcher.BeginInvoke(() =>
            MessageBox.Show(
                $"Не удалось загрузить модель распознавания:\n{message}\n\nПереустановите приложение.",
                "Govorun", MessageBoxButton.OK, MessageBoxImage.Error));
        dictation.RecordingFailed += message => Dispatcher.BeginInvoke(() =>
            tray.ShowNotification("Govorun", $"Не удалось начать запись: {message}"));

        tray.HistoryRequested += () => Dispatcher.BeginInvoke(() => _historyWindow!.ShowOrActivate());
        tray.ExitRequested += () =>
        {
            Log.Information("Exit requested");
            Shutdown();
        };
        tray.AutoStartToggled += enabled => ApplyAutoStart(enabled);
        tray.MicSelected += id => ApplyMic(id, notify: true);
        tray.HotkeySelected += mode => ApplyHotkey(mode, notify: true);
        tray.ActivationSelected += mode => ApplyActivation(mode, notify: true);

        var history = _historyWindow!;
        history.AutoStartToggled += enabled => ApplyAutoStart(enabled);
        history.MicSelected += id => ApplyMic(id, notify: false);
        history.HotkeySelected += mode => ApplyHotkey(mode, notify: false);
        history.ActivationSelected += mode => ApplyActivation(mode, notify: false);
    }

    private void ApplyAutoStart(bool enabled)
    {
        _settings.AutoStart = enabled;
        _settings.Save();
        TrySetAutoStart(enabled);
        SyncTray();
    }

    private void ApplyMic(string? id, bool notify)
    {
        _settings.MicDeviceId = id;
        _settings.Save();
        _dictation!.MicDeviceId = id;
        if (notify) _tray!.ShowNotification("Govorun", id is null ? "Микрофон: системный по умолчанию" : "Микрофон переключён");
        SyncTray();
    }

    private void ApplyHotkey(HotkeyMode mode, bool notify)
    {
        _settings.Hotkey = mode;
        _settings.Save();
        _hotkeys!.Mode = mode;
        if (notify)
            _tray!.ShowNotification("Govorun", "Горячая клавиша: " + mode switch
            {
                HotkeyMode.CtrlWin => "Ctrl+Win",
                HotkeyMode.CtrlSpace => "Ctrl+Space",
                _ => "двойной Ctrl",
            });
        SyncTray();
    }

    private void ApplyActivation(ActivationMode mode, bool notify)
    {
        _settings.Activation = mode;
        _settings.Save();
        _hotkeys!.Activation = mode;
        if (notify)
            _tray!.ShowNotification("Govorun", mode == ActivationMode.PushToTalk
                ? "Режим: держу — записывает, отпустил — стоп"
                : "Режим: нажал — запись, нажал ещё раз — стоп");
        SyncTray();
    }

    private void SyncTray() =>
        _tray?.SyncSettings(_settings.MicDeviceId, _settings.Hotkey, _settings.Activation, _settings.AutoStart);

    internal static void TrySetAutoStart(bool enabled)
    {
        try { AutoStart.Set(enabled); }
        catch (Exception ex) { Log.Warning(ex, "Autostart update failed"); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdog?.Stop();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _dictation?.Dispose();
        Log.CloseAndFlush();
        // Only the instance that actually acquired the mutex may release it — a second
        // instance takes the Shutdown() path above and would throw ApplicationException here.
        if (_ownsMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { /* already released or never owned */ }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}

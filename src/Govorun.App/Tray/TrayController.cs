using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Govorun.App.Services;
using Govorun.Core.Audio;
using Govorun.Core.Hotkeys;
using Govorun.Core.Support;
using H.NotifyIcon;

namespace Govorun.App.Tray;

/// <summary>
/// Tray-first UI: the parrot icon (static — recording state is shown by the
/// overlay, not the tray) and the context menu with dictation history,
/// controls, mic selection, autostart and exit.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _micMenu;
    private readonly MenuItem _controlsMenu;
    private readonly MenuItem _autostartItem;
    private readonly MenuItem _updateItem;
    private string? _currentMicId;
    private HotkeyMode _currentHotkey;
    private ActivationMode _currentActivation;

    public event Action? ExitRequested;
    public event Action<bool>? AutoStartToggled;
    /// <summary>Raised with the chosen capture device ID (null = system default).</summary>
    public event Action<string?>? MicSelected;
    public event Action<HotkeyMode>? HotkeySelected;
    public event Action<ActivationMode>? ActivationSelected;
    public event Action? HistoryRequested;
    public event Action? DonateRequested;
    public event Action? UpdateRequested;

    public TrayController(bool autoStartEnabled, string? currentMicId, HotkeyMode hotkey, ActivationMode activation)
    {
        _currentMicId = currentMicId;
        _currentHotkey = hotkey;
        _currentActivation = activation;

        var historyItem = new MenuItem { Header = "История" };
        historyItem.Click += (_, _) => HistoryRequested?.Invoke();

        _micMenu = new MenuItem { Header = "Микрофон" };
        _micMenu.SubmenuOpened += (_, _) => RebuildMicMenu();
        RebuildMicMenu();

        _controlsMenu = new MenuItem { Header = "Управление" };
        RebuildControlsMenu();

        _autostartItem = new MenuItem { Header = "Запускать при входе в Windows", IsCheckable = true, IsChecked = autoStartEnabled };
        _autostartItem.Click += (_, _) => AutoStartToggled?.Invoke(_autostartItem.IsChecked);

        // Hidden until a check finds something newer — see ShowUpdateAvailable.
        _updateItem = new MenuItem { Header = "Доступно обновление", Visibility = Visibility.Collapsed };
        _updateItem.Click += (_, _) => UpdateRequested?.Invoke();

        var exitItem = new MenuItem { Header = "Выход" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(historyItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_micMenu);
        menu.Items.Add(_controlsMenu);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_updateItem);
        // Only when a real donation page is configured, so we never offer a dead link.
        if (Links.DonationConfigured)
        {
            var donateItem = new MenuItem { Header = "Поддержать разработку ♥" };
            donateItem.Click += (_, _) => DonateRequested?.Invoke();
            menu.Items.Add(donateItem);
        }
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            Icon = TrayIcons.App,
            ToolTipText = "Govorun — загрузка модели…",
            ContextMenu = menu,
        };
        _icon.ForceCreate();

        // Left click opens the history/settings window directly — right click
        // still opens the context menu for people who already know it.
        _icon.TrayLeftMouseUp += (_, _) => HistoryRequested?.Invoke();
    }

    /// <summary>Keeps the context menu's checkmarks in sync when settings change
    /// from elsewhere (the history window's Settings tab).</summary>
    public void SyncSettings(string? micId, HotkeyMode hotkey, ActivationMode activation, bool autoStart)
    {
        _currentMicId = micId;
        _currentHotkey = hotkey;
        _currentActivation = activation;
        _autostartItem.IsChecked = autoStart;
        RebuildControlsMenu();
    }

    public void SetState(DictationState state)
    {
        _icon.ToolTipText = state switch
        {
            DictationState.Loading => "Govorun — загрузка модели…",
            DictationState.Recording => "Govorun — запись…",
            DictationState.Transcribing => "Govorun — обработка…",
            _ => "Govorun — готов (нажмите горячую клавишу и говорите)",
        };
    }

    private void RebuildMicMenu()
    {
        _micMenu.Items.Clear();

        var defaultItem = new MenuItem { Header = "Системный по умолчанию", IsCheckable = true, IsChecked = _currentMicId is null };
        defaultItem.Click += (_, _) => SelectMic(null);
        _micMenu.Items.Add(defaultItem);
        _micMenu.Items.Add(new Separator());

        try
        {
            foreach (var device in AudioRecorder.ListDevices())
            {
                var item = new MenuItem { Header = device.Name, IsCheckable = true, IsChecked = device.Id == _currentMicId };
                var id = device.Id;
                item.Click += (_, _) => SelectMic(id);
                _micMenu.Items.Add(item);
            }
        }
        catch
        {
            _micMenu.Items.Add(new MenuItem { Header = "Устройства не найдены", IsEnabled = false });
        }
    }

    private void SelectMic(string? id)
    {
        _currentMicId = id;
        MicSelected?.Invoke(id);
    }

    private void RebuildControlsMenu()
    {
        _controlsMenu.Items.Clear();

        void AddHotkey(string header, HotkeyMode mode)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = _currentHotkey == mode };
            item.Click += (_, _) =>
            {
                _currentHotkey = mode;
                HotkeySelected?.Invoke(mode);
                RebuildControlsMenu();
            };
            _controlsMenu.Items.Add(item);
        }

        void AddActivation(string header, ActivationMode mode)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = _currentActivation == mode };
            item.Click += (_, _) =>
            {
                _currentActivation = mode;
                ActivationSelected?.Invoke(mode);
                RebuildControlsMenu();
            };
            _controlsMenu.Items.Add(item);
        }

        AddHotkey("Ctrl + Win", HotkeyMode.CtrlWin);
        AddHotkey("Ctrl + Space", HotkeyMode.CtrlSpace);
        AddHotkey("Двойной Ctrl", HotkeyMode.DoubleCtrl);
        _controlsMenu.Items.Add(new Separator());
        AddActivation("Держу — запись, отпустил — стоп", ActivationMode.PushToTalk);
        AddActivation("Нажал — запись, ещё раз — стоп", ActivationMode.Toggle);
    }

    /// <summary>Reveals the update entry in the menu and names the version.</summary>
    public void ShowUpdateAvailable(Version version)
    {
        _updateItem.Header = $"Доступно обновление {version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
        _updateItem.Visibility = Visibility.Visible;
    }

    public void ShowNotification(string title, string message) =>
        _icon.ShowNotification(title, message);

    public void Dispose() => _icon.Dispose();
}

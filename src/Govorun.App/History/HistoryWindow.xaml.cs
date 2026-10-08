using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Govorun.Core.Audio;
using Govorun.Core.History;
using Govorun.Core.Hotkeys;
using Govorun.Core.Settings;
using Govorun.Core.Support;

namespace Govorun.App.History;

/// <summary>
/// The tray-first window: a brand-styled list of recent dictations with a
/// one-click copy per entry (the recovery net for "I dictated it but it
/// landed nowhere"), plus a Settings tab so mic/hotkey/mode/autostart don't
/// require knowing to right-click the tray icon. Reused as a singleton —
/// <see cref="ShowOrActivate"/> reopens the same instance instead of stacking
/// windows.
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly HistoryStore _store;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _relativeTimeTicker;
    private List<MicDevice> _micDevices = new();
    private bool _suppressSettingsEvents;

    public event Action<string?>? MicSelected;
    public event Action<HotkeyMode>? HotkeySelected;
    public event Action<ActivationMode>? ActivationSelected;
    public event Action<bool>? AutoStartToggled;
    public event Action<bool>? CheckForUpdatesToggled;
    public event Action? DonateRequested;

    public HistoryWindow(HistoryStore store, AppSettings settings)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        // No matching unsubscribe: OnClosing cancels the close and hides instead, so
        // this window and the store both live for the whole process. A Closed handler
        // here would never run.
        _store.Changed += OnStoreChanged;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        // Relative timestamps ("2 мин назад") age out — refresh them periodically
        // while the window is open, without rebuilding the whole list.
        _relativeTimeTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _relativeTimeTicker.Tick += (_, _) => RefreshTimestamps();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _relativeTimeTicker.Start();
            else _relativeTimeTicker.Stop();
        };

        Rebuild();
        ShowHistoryTab();
    }

    public void ShowOrActivate()
    {
        Rebuild();
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Hide();

    private void OnHistoryTabClicked(object sender, RoutedEventArgs e) => ShowHistoryTab();

    private void OnSettingsTabClicked(object sender, RoutedEventArgs e) => ShowSettingsTab();

    private void ShowHistoryTab()
    {
        HistoryPanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        SetTabActive(HistoryTabButton, true);
        SetTabActive(SettingsTabButton, false);
    }

    private void ShowSettingsTab()
    {
        RebuildSettingsTab();
        HistoryPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        SetTabActive(HistoryTabButton, false);
        SetTabActive(SettingsTabButton, true);
    }

    private void SetTabActive(Button button, bool active)
    {
        button.Background = active ? new SolidColorBrush(Color.FromRgb(0xED, 0xF2, 0xFC)) : Brushes.Transparent;
        button.Foreground = active
            ? (Brush)FindResource("BrandBlue")
            : (Brush)FindResource("BrandMuted");
    }

    private void RebuildSettingsTab()
    {
        _suppressSettingsEvents = true;

        MicCombo.Items.Clear();
        _micDevices = new List<MicDevice> { new("", "Системный по умолчанию", true) };
        try { _micDevices.AddRange(AudioRecorder.ListDevices()); }
        catch { /* device enumeration can fail transiently — default stays selectable */ }
        int selected = 0;
        for (int i = 0; i < _micDevices.Count; i++)
        {
            MicCombo.Items.Add(_micDevices[i].Name);
            if ((_micDevices[i].Id.Length == 0 && _settings.MicDeviceId is null) || _micDevices[i].Id == _settings.MicDeviceId)
                selected = i;
        }
        MicCombo.SelectedIndex = selected;

        HotkeyCtrlWin.IsChecked = _settings.Hotkey == HotkeyMode.CtrlWin;
        HotkeyCtrlSpace.IsChecked = _settings.Hotkey == HotkeyMode.CtrlSpace;
        HotkeyDoubleCtrl.IsChecked = _settings.Hotkey == HotkeyMode.DoubleCtrl;
        ModeHold.IsChecked = _settings.Activation == ActivationMode.PushToTalk;
        ModeToggle.IsChecked = _settings.Activation == ActivationMode.Toggle;
        AutoStartCheck.IsChecked = _settings.AutoStart;
        UpdateCheck.IsChecked = _settings.CheckForUpdates;
        DonatePanel.Visibility = Links.DonationConfigured ? Visibility.Visible : Visibility.Collapsed;

        _suppressSettingsEvents = false;
    }

    private void OnMicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingsEvents || MicCombo.SelectedIndex < 0) return;
        var id = _micDevices[MicCombo.SelectedIndex].Id;
        MicSelected?.Invoke(id.Length == 0 ? null : id);
    }

    private void OnControlChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        HotkeySelected?.Invoke(HotkeyCtrlSpace.IsChecked == true ? HotkeyMode.CtrlSpace
            : HotkeyDoubleCtrl.IsChecked == true ? HotkeyMode.DoubleCtrl
            : HotkeyMode.CtrlWin);
        ActivationSelected?.Invoke(ModeToggle.IsChecked == true ? ActivationMode.Toggle : ActivationMode.PushToTalk);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        AutoStartToggled?.Invoke(AutoStartCheck.IsChecked == true);
    }

    private void OnUpdateCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingsEvents) return;
        CheckForUpdatesToggled?.Invoke(UpdateCheck.IsChecked == true);
    }

    private void OnDonateClicked(object sender, RoutedEventArgs e) => DonateRequested?.Invoke();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Keep the singleton alive — hide instead of destroying, so history
        // stays instantly reachable from the tray without reloading.
        e.Cancel = true;
        Hide();
    }

    private void OnStoreChanged() => Dispatcher.BeginInvoke(Rebuild);

    private void Rebuild()
    {
        var entries = _store.Entries;
        EmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EntriesList.Items.Clear();
        foreach (var entry in entries)
            EntriesList.Items.Add(BuildCard(entry));
    }

    private void RefreshTimestamps()
    {
        foreach (var card in EntriesList.Items.OfType<Border>())
            if (card.Tag is DateTime utc && card.Child is Grid grid && grid.Children[0] is StackPanel sp &&
                sp.Children[0] is TextBlock timeLabel)
                timeLabel.Text = FormatRelative(utc);
    }

    private Border BuildCard(HistoryEntry entry)
    {
        var timeLabel = new TextBlock
        {
            Text = FormatRelative(entry.TimestampUtc),
            FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("BrandMuted"),
        };

        var textBlock = new TextBlock
        {
            Text = entry.Text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("BrandInk"),
        };

        var textColumn = new StackPanel();
        textColumn.Children.Add(timeLabel);
        textColumn.Children.Add(textBlock);

        var copyButton = new Button
        {
            Content = CopyIcon(),
            Style = (Style)FindResource("BrandIconButton"),
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Скопировать",
        };
        copyButton.Click += (_, _) => CopyWithFeedback(entry.Text, copyButton, timeLabel, entry.TimestampUtc);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textColumn, 0);
        Grid.SetColumn(copyButton, 1);
        grid.Children.Add(textColumn);
        grid.Children.Add(copyButton);

        return new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0xE9, 0xF7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 10, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Tag = entry.TimestampUtc,
            Child = grid,
        };
    }

    private void CopyWithFeedback(string text, Button button, TextBlock timeLabel, DateTime utc)
    {
        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
        }
        catch
        {
            return; // clipboard busy — the click simply had no effect, nothing to recover
        }

        timeLabel.Text = "Скопировано";
        timeLabel.Foreground = (System.Windows.Media.Brush)FindResource("BrandBlue");
        var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        revert.Tick += (_, _) =>
        {
            revert.Stop();
            timeLabel.Text = FormatRelative(utc);
            timeLabel.Foreground = (System.Windows.Media.Brush)FindResource("BrandMuted");
        };
        revert.Start();
    }

    private static UIElement CopyIcon()
    {
        // Small two-rectangle "copy" glyph — no external asset needed.
        var stroke = new SolidColorBrush(Color.FromRgb(0x61, 0x70, 0x8F));
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new Rectangle
        {
            Width = 9, Height = 11, RadiusX = 2, RadiusY = 2,
            Stroke = stroke, StrokeThickness = 1.4,
            Fill = System.Windows.Media.Brushes.Transparent,
        });
        Canvas.SetLeft((UIElement)canvas.Children[0], 5);
        Canvas.SetTop((UIElement)canvas.Children[0], 3.5);
        canvas.Children.Add(new Rectangle
        {
            Width = 9, Height = 11, RadiusX = 2, RadiusY = 2,
            Stroke = stroke, StrokeThickness = 1.4,
            Fill = new SolidColorBrush(Color.FromRgb(0xFB, 0xFC, 0xFF)),
        });
        Canvas.SetLeft((UIElement)canvas.Children[1], 1.5);
        Canvas.SetTop((UIElement)canvas.Children[1], 1);
        return canvas;
    }

    private static string FormatRelative(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var age = DateTime.UtcNow - utc;
        if (age < TimeSpan.FromMinutes(1)) return "только что";
        if (age < TimeSpan.FromMinutes(60)) return $"{(int)age.TotalMinutes} мин назад";
        if (age < TimeSpan.FromHours(24) && local.Date == DateTime.Now.Date) return $"сегодня, {local:HH:mm}";
        if (local.Date == DateTime.Now.Date.AddDays(-1)) return $"вчера, {local:HH:mm}";
        return local.ToString("d MMM, HH:mm");
    }
}

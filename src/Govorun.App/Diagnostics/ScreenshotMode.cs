using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Govorun.App.Donation;
using Govorun.App.History;
using Govorun.App.Overlay;
using Govorun.Core.History;
using Govorun.Core.Settings;

namespace Govorun.App.Diagnostics;

/// <summary>
/// Renders the app's windows to PNG files for the landing page and the README
/// (<c>Govorun.App.exe --shots &lt;dir&gt;</c>).
///
/// Uses <see cref="RenderTargetBitmap"/> rather than capturing the screen: the result
/// has no desktop behind it, so the recording capsule comes out with a real alpha
/// channel and a designer can place it over a screenshot of Word. It also renders at
/// 2x, which is what a retina-ready page needs.
///
/// Nothing here touches %APPDATA%: the history store points at a temp file and the
/// settings object is never saved.
/// </summary>
internal static class ScreenshotMode
{
    private const double Scale = 2.0;

    internal static void Run(string outputDirectory)
    {
        var dir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(dir);

        CaptureOverlay(dir);
        CaptureHistoryAndSettings(dir);
        CaptureDonation(dir);

        Console.WriteLine($"Screenshots written to {dir}");
    }

    private static void CaptureOverlay(string dir)
    {
        var bubble = new BubbleWindow();
        // Off-screen: the capsule normally sits at the bottom centre, and we don't want
        // it flashing over whatever the developer is looking at.
        bubble.Left = -10_000;
        bubble.Top = -10_000;
        bubble.ShowRecording(() => TimeSpan.FromSeconds(7));

        // One PNG per loudness, because the equalizer is the thing people ask to see.
        // The bars chase their target with a spring, so each level needs a moment of
        // animation before it looks like itself.
        foreach (var (name, level) in new[] { ("quiet", 0.02f), ("mid", 0.12f), ("loud", 0.45f) })
        {
            for (int i = 0; i < 25; i++)
            {
                bubble.PushLevel(level);
                Pump(33);
            }
            Save(bubble, Path.Combine(dir, $"overlay-recording-{name}.png"));
        }

        bubble.ShowTranscribing();
        Pump(300);
        Save(bubble, Path.Combine(dir, "overlay-transcribing.png"));

        bubble.HideBubble();
        bubble.Close();
    }

    private static void CaptureHistoryAndSettings(string dir)
    {
        // Seeded through the file rather than Add(), which would stamp everything with
        // "now" and fill the list with "только что".
        var now = DateTime.UtcNow;
        var entries = new List<HistoryEntry>
        {
            new(now.AddMinutes(-3), "25% читателей библиотеки пользуются электронным каталогом."),
            new(now.AddMinutes(-38), "Аннотация: сборник статей по истории книжного дела, рассчитан на студентов и библиографов."),
            new(now.AddHours(-5), "Запишите, пожалуйста, что в фонде осталось 543 экземпляра, и подготовьте сводку к четвергу."),
            new(now.AddDays(-1).AddHours(-2), "Добрый день! Высылаю список новых поступлений за сентябрь, обратите внимание на раздел краеведения."),
            new(now.AddDays(-3), "Тема доклада: цифровые сервисы в работе современной библиотеки."),
        };
        var storePath = Path.Combine(Path.GetTempPath(), $"govorun-shots-{Guid.NewGuid():N}.json");
        File.WriteAllText(storePath, System.Text.Json.JsonSerializer.Serialize(entries));

        var store = new HistoryStore(storePath);
        var settings = new AppSettings { OnboardingCompleted = true };
        var window = new HistoryWindow(store, settings)
        {
            Left = -10_000,
            Top = -10_000,
        };
        window.Show();
        Pump(400);
        Save(window, Path.Combine(dir, "history.png"));

        // The tab switcher is private; clicking the button is how a user gets there.
        window.SettingsTabButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        // The settings tab is taller than the default window, and a screenshot with a
        // clipped block and a scrollbar reads as unfinished. The window is resizable,
        // so this is a state a user can have.
        window.Height = 700;
        Pump(400);
        Save(window, Path.Combine(dir, "settings.png"));

        window.Close();
        try { File.Delete(storePath); } catch { /* temp file */ }
    }

    private static void CaptureDonation(string dir)
    {
        var settings = new AppSettings
        {
            DictationCount = 214,
            WordsDictated = 31_480,
            FirstRunUtc = DateTime.UtcNow.AddDays(-45),
        };
        var window = new ThanksWindow(settings)
        {
            Left = -10_000,
            Top = -10_000,
        };
        window.Show();
        Pump(300);
        Save(window, Path.Combine(dir, "donation.png"));
        window.Close();
    }

    /// <summary>
    /// Lets the dispatcher run for <paramref name="milliseconds"/> so animation timers
    /// tick. Rendering straight after Show() would catch the bars at their start height.
    /// </summary>
    private static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        int width = (int)Math.Ceiling(window.ActualWidth * Scale);
        int height = (int)Math.Ceiling(window.ActualHeight * Scale);
        if (width <= 0 || height <= 0) throw new InvalidOperationException($"{window.GetType().Name} has no size yet.");

        var bitmap = new RenderTargetBitmap(width, height, 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        Console.WriteLine($"  {Path.GetFileName(path)}  {width}x{height}");
    }
}

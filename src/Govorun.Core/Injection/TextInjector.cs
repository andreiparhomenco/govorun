using System.Windows;
using System.Windows.Threading;

namespace Govorun.Core.Injection;

/// <summary>
/// Inserts transcribed text into the currently focused control.
/// Primary path: save clipboard → set text → Ctrl+V → restore clipboard
/// (preserving images/files). Fallback for consoles and protected fields:
/// per-character SendInput typing with KEYEVENTF_UNICODE.
/// </summary>
public static class TextInjector
{
    // Window classes where Ctrl+V is unreliable or dangerous — type instead.
    private static readonly string[] TypingModeClasses =
    {
        "ConsoleWindowClass", // classic conhost terminals
        "PuTTY",
        "VirtualConsoleClass", // ConEmu
    };

    public static void Inject(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // WPF Clipboard requires an STA thread; transcription finishes on a worker.
        // The STA thread also needs a real message pump: Clipboard.SetDataObject's
        // OLE flush (OleSetClipboard/OleFlushClipboard) can corrupt memory and crash
        // the whole process with an access violation on a non-pumping STA thread —
        // confirmed via a Windows Error Reporting dump (0xc0000005 in ole32.dll,
        // inside RestoreClipboard's Clipboard.Flush) when restoring non-empty prior
        // clipboard content. Dispatcher.Run() gives the thread that pump.
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(() =>
                {
                    try { Inject(text); }
                    finally { dispatcher.InvokeShutdown(); }
                });
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return;
        }

        var windowClass = NativeInput.GetForegroundWindowClass();
        if (TypingModeClasses.Any(c => windowClass.Contains(c, StringComparison.OrdinalIgnoreCase)))
        {
            NativeInput.TypeText(text);
            return;
        }

        try
        {
            InjectViaClipboard(text);
        }
        catch
        {
            // Clipboard can be locked by another process; typing always works.
            NativeInput.TypeText(text);
        }
    }

    /// <summary>Exposed for diagnostics (CLI inject-debug).</summary>
    public static void InjectByTyping(string text) => NativeInput.TypeText(text);

    /// <summary>Exposed for diagnostics (CLI inject-debug).</summary>
    public static void SendPasteShortcut() => NativeInput.SendCtrlV();

    private static void InjectViaClipboard(string text)
    {
        var saved = SaveClipboard();
        try
        {
            SetClipboardTextWithRetry(text);
            NativeInput.SendCtrlV();
            // Give the target app time to read the clipboard before we restore it:
            // ~150 ms covers native apps, the rest is headroom for Electron ones
            // (VS Code, Obsidian) that read it asynchronously.
            Thread.Sleep(500);
        }
        finally
        {
            RestoreClipboard(saved);
        }
    }

    private static void SetClipboardTextWithRetry(string text)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // copy:true flushes to the OS clipboard. copy:false would require our
                // STA thread to pump messages while the target app requests the data —
                // and we are asleep in Thread.Sleep during the paste.
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true);
                return;
            }
            catch when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static DataObject? SaveClipboard()
    {
        try
        {
            var current = Clipboard.GetDataObject();
            if (current is null) return null;

            // Copy data out of the live IDataObject so it survives our overwrite.
            var snapshot = new DataObject();
            foreach (var format in current.GetFormats(false))
            {
                try
                {
                    if (current.GetDataPresent(format, false))
                    {
                        var data = current.GetData(format, false);
                        if (data is not null) snapshot.SetData(format, data);
                    }
                }
                catch
                {
                    // Some formats are lazily rendered by dead processes — skip them.
                }
            }
            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreClipboard(DataObject? saved)
    {
        if (saved is null || saved.GetFormats(false).Length == 0)
            return;
        try
        {
            Clipboard.SetDataObject(saved, copy: true);
        }
        catch
        {
            // Restoring is best-effort; never fail the injection over it.
        }
    }
}

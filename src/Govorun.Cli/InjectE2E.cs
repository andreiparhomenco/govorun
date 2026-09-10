using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Govorun.Core.Injection;

namespace Govorun.Cli;

/// <summary>
/// End-to-end regression test of TextInjector without a microphone:
/// puts a bitmap into the clipboard, opens Notepad, injects text via the real
/// clipboard+Ctrl+V path, reads the Edit control back, and verifies the
/// original clipboard bitmap was restored.
/// </summary>
internal static class InjectE2E
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowTitle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SendMessage(IntPtr hWnd, uint msg, int wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    private const uint WM_GETTEXT = 0x000D;

    private static string ForegroundClass()
    {
        var sb = new StringBuilder(256);
        GetClassName(GetForegroundWindow(), sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Step-by-step diagnostics: typing path, then clipboard path, each read back.</summary>
    public static int Debug()
    {
        int exitCode = 1;
        var thread = new Thread(() =>
        {
            Process? notepad = null;
            try
            {
                notepad = Process.Start("notepad.exe");
                notepad.WaitForInputIdle(5000);
                Thread.Sleep(500);
                SetForegroundWindow(notepad.MainWindowHandle);
                Thread.Sleep(300);
                var edit = FindWindowEx(notepad.MainWindowHandle, IntPtr.Zero, "Edit", null);
                Console.WriteLine($"edit hwnd=0x{edit:X}, fg='{ForegroundClass()}'");

                // Step A: typing path (SendInput KEYEVENTF_UNICODE).
                TextInjector.InjectByTyping("abc");
                Thread.Sleep(400);
                Console.WriteLine($"after typing: '{ReadEdit(edit)}'");

                // Step B: plain clipboard set + Ctrl+V.
                Clipboard.SetText("XYZ");
                Console.WriteLine($"clipboard now: '{Clipboard.GetText()}'");
                TextInjector.SendPasteShortcut();
                Thread.Sleep(400);
                Console.WriteLine($"after paste: '{ReadEdit(edit)}'");
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"debug failed: {ex}");
            }
            finally
            {
                if (notepad is not null && !notepad.HasExited) notepad.Kill();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static string ReadEdit(IntPtr edit)
    {
        var buffer = new StringBuilder(4096);
        SendMessage(edit, WM_GETTEXT, buffer.Capacity, buffer);
        return buffer.ToString();
    }

    public static int Run(string text)
    {
        int exitCode = 1;
        var thread = new Thread(() => exitCode = RunSta(text));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunSta(string text)
    {
        Process? notepad = null;
        try
        {
            // 1. Seed the clipboard with a bitmap — injection must restore it.
            var bitmap = BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
                new byte[16] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 }, 8);
            Clipboard.SetImage(bitmap);
            Console.WriteLine("Clipboard seeded with a bitmap");

            // 2. Launch Notepad and focus it.
            notepad = Process.Start("notepad.exe");
            if (!notepad.WaitForInputIdle(5000)) throw new InvalidOperationException("Notepad did not become idle");
            Thread.Sleep(500);
            bool fgOk = SetForegroundWindow(notepad.MainWindowHandle);
            Thread.Sleep(300);
            Console.WriteLine($"SetForegroundWindow={fgOk}, hwnd=0x{notepad.MainWindowHandle:X}, foreground class='{ForegroundClass()}'");

            // 3. Inject through the production path.
            TextInjector.Inject(text);
            Thread.Sleep(500);

            // 4. Read the Edit control content back.
            var edit = FindWindowEx(notepad.MainWindowHandle, IntPtr.Zero, "Edit", null);
            if (edit == IntPtr.Zero) throw new InvalidOperationException("Notepad Edit control not found");
            var buffer = new StringBuilder(4096);
            SendMessage(edit, WM_GETTEXT, buffer.Capacity, buffer);
            var actual = buffer.ToString();

            bool textOk = actual == text;
            bool clipboardOk = Clipboard.ContainsImage();

            Console.WriteLine($"Text in Notepad:   {(textOk ? "OK" : $"FAIL (got '{actual}')")}");
            Console.WriteLine($"Clipboard restore: {(clipboardOk ? "OK (bitmap present)" : "FAIL (bitmap lost)")}");
            return textOk && clipboardOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"inject e2e failed: {ex.Message}");
            return 1;
        }
        finally
        {
            if (notepad is not null && !notepad.HasExited)
                notepad.Kill(); // discard the unsaved buffer
        }
    }
}

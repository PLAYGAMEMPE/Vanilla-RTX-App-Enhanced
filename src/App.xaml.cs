using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Vanilla_RTX_App.Core;
using WinUIEx;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Vanilla_RTX_App;
/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private static Mutex? _mutex = null;
    private static EventWaitHandle? _wakeEvent = null;
    private static int _fatalErrorReported;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            ReportFatalError("Application initialization", ex);
            throw;
        }

        TraceManager.Initialize();
        _ = OnlineTexts.TriggerUpdateAsync(); // Silent PSA Update, hopefully by the time the startup sequence is finished, we have new PSAs to show!


        // 1. Catches unhandled exceptions on the UI thread from any window
        this.UnhandledException += (s, e) =>
        {
            ReportFatalError("UI Thread", e.Exception);
            // intentionally not setting e.Handled = true
            // let it crash naturally so WER still gets the dump
        };

        // 2. Catches exceptions escaping async void after an await,
        // and anything thrown on the UI thread that XAML doesn't intercept
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            WriteCrashLog("Unobserved Task", e.Exception.Message, e.Exception.ToString());
            e.SetObserved(); // prevents process termination for tasks,
                             // since we've logged it ourselves
        };

        // 3. Catches exceptions on background threads, Thread.Start, etc.
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            if (ex != null)
                ReportFatalError("Background Thread", ex);
            else
                WriteCrashLog("Background Thread", "Unknown", e.ExceptionObject?.ToString() ?? "No details");
            // can't prevent termination here, but log is written
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        bool isNewInstance;
        _mutex = new Mutex(true, GetUniqueName(), out isNewInstance);

        if (!isNewInstance)
        {
            // Signal the existing instance to bring itself to front
            if (EventWaitHandle.TryOpenExisting($"{GetUniqueName()}_wake", out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            Exit();
            return;
        }

        // Create the wake event for this instance to listen on
        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"{GetUniqueName()}_wake");
        _ = Task.Run(() =>
        {
            while (_wakeEvent.WaitOne())
            {
                MainWindow.Instance?.DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.Instance.Restore();            // un-minimizes/un-maximizes, WinUIEx
                    MainWindow.Instance.SetForegroundWindow(); // brings to foreground, WinUIEx
                });
            }
        });

        // Load the persisted language choice and apply it BEFORE the window/its XAML
        // gets constructed below - resource strings (x:Uid, ResourceLoader) resolve
        // once at construction time, so this has to run first for the override to
        // actually affect what shows up. MainWindow_Loaded calls LoadSettings()
        // again later for everything else; re-loading here too is harmless.
        EnvironmentVariables.LoadSettings();
        Core.LanguageService.ApplyLanguage(Core.LanguageService.ResolveSavedLanguage());

        // Brief delay before Activate() to allow InitializeComponent() and lamp animators
        // to finish rendering before the window becomes visible, preventing a black background briefly appearing or splash images not loading in time.
        _window = new MainWindow();
        await Task.Delay(175); // A delay ensures the xaml is constructed before window tries to appear.
        _window.Activate();
    }

    private static void ReportFatalError(string source, Exception exception)
    {
        if (Interlocked.Exchange(ref _fatalErrorReported, 1) != 0)
            return;

        WriteCrashLog(
            source,
            $"[{exception.GetType().FullName} / 0x{exception.HResult:X8}] {exception.Message}",
            exception.ToString());

        var logPath = Path.Combine(Core.AppStorage.LocalFolderPath, "last_session_crash_log.txt");
        var text =
            "Vanilla RTX App no pudo iniciarse correctamente.\n" +
            "The application could not start correctly.\n\n" +
            $"{exception.Message}\n\n" +
            "Diagnostico / diagnostic log:\n" +
            logPath;

        try
        {
            MessageBoxW(IntPtr.Zero, text, "Vanilla RTX App - Startup error", 0x00000010);
        }
        catch
        {
            // The crash log remains available if even the native fallback dialog fails.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);

    public static void WriteCrashLog(string source, string message, string detail)
    {
        try
        {
            var logPath = Path.Combine(
                Core.AppStorage.LocalFolderPath,
                "last_session_crash_log.txt");

            File.AppendAllText(logPath,
                $"=== Crash Report ===\n" +
                $"Version:   {EnvironmentVariables.appVersion ?? "unknown"}\n" +
                $"Source:    {source}\n" +
                $"Time:      {DateTime.Now}\n" +
                $"Message:   {message}\n" +
                $"Executable: {Environment.ProcessPath ?? "unknown"}\n" +
                $"BaseDir:    {AppContext.BaseDirectory}\n" +
                $"WorkingDir: {Environment.CurrentDirectory}\n" +
                $"WinAppSDK:  {Environment.GetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY") ?? "not set"}\n" +
                $"WinAppPID:  {Environment.GetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID") ?? "not set"}\n" +
                $"Detail:\n{detail}\n\n" +
                $"{TraceManager.GetAllTraceLogs()}\n\n");
        }
        catch { /* Last-resort logging must never throw. */ }
    }

    // A fixed name is enough for its only purpose (a single-instance mutex/wake-event) -
    // but it must still differ between the packaged (MSIX, dev-mode) and unpackaged
    // (portable .exe) builds. They're two independent, simultaneously-runnable install
    // methods during development - the packaged copy from modo-desarrollo.bat and a
    // portable .exe from compilar-portable.bat both exist on the same machine at once -
    // and sharing one mutex name means whichever one is already running (even sitting in
    // the background, easy to forget about) silently steals every subsequent launch of
    // the OTHER one: OnLaunched sees the mutex already held, brings the stale existing
    // window to the foreground instead of opening a new one, and exits - so a freshly
    // rebuilt dev-mode package can look like it's "not showing any changes" when what's
    // actually happening is it never even opened, and you're still looking at an old
    // portable-build window. This was a real regression (a literal single fixed name used
    // to be unpackaged-only; packaged builds used to derive their own name from
    // Package.Current.Id.FamilyName, which incidentally kept the two apart).
    public static string GetUniqueName() =>
        Core.PackageContext.IsPackaged ? "vanilla_rtx_app_packaged" : "vanilla_rtx_app_portable";

    /// <summary>
    /// Releases the single-instance mutex before a replacement process starts.
    /// AppInstance.Restart launches the new process while this one is still alive;
    /// keeping the mutex until process teardown makes that replacement identify as
    /// a duplicate and exit before it can show a window.
    /// </summary>
    internal static void ReleaseSingleInstanceLockForRestart()
    {
        var mutex = _mutex;
        _mutex = null;
        if (mutex == null)
            return;

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The process can still be relaunched if ownership was already released.
        }
        finally
        {
            mutex.Dispose();
        }
    }

    public static Windows.ApplicationModel.PackageVersion GetPackageVersion()
    {
        try
        {
            // Reads the version straight off the built assembly instead of
            // Package.Current.Id.Version, which requires package identity and throws when
            // running unpackaged (a portable .exe with no installer) - this works
            // identically either way. Keep AssemblyVersion in the csproj in sync with the
            // version shown to users; it's the single source of truth for both now.
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                    ?? new Version(0, 0, 0, 0);
            return new Windows.ApplicationModel.PackageVersion
            {
                Major = (ushort)Math.Max(0, v.Major),
                Minor = (ushort)Math.Max(0, v.Minor),
                Build = (ushort)Math.Max(0, v.Build),
                Revision = (ushort)Math.Max(0, v.Revision),
            };
        }
        catch
        {
            Trace.WriteLine("[GetAppVersion] Failed.");
            return new Windows.ApplicationModel.PackageVersion { Major = 0, Minor = 0, Build = 0, Revision = 0 };
        }
    }
}




/// <summary>
/// Custom TraceListener that captures all Trace.WriteLine calls
/// </summary>
public class InMemoryTraceListener : TraceListener
{
    private readonly ConcurrentQueue<TraceEntry> _entries = new();
    private readonly int _maxEntries;
    private int _count;

    public InMemoryTraceListener(int maxEntries = 1000)
    {
        _maxEntries = maxEntries;
    }

    public override void Write(string? message)
    {
        // Usually not used, but implement for completeness
        WriteLine(message);
    }

    public override void WriteLine(string? message)
    {
        var entry = new TraceEntry
        {
            Timestamp = DateTime.Now,
            Message = message,
            ThreadId = Environment.CurrentManagedThreadId
        };

        _entries.Enqueue(entry);
        if (Interlocked.Increment(ref _count) > _maxEntries)
        {
            if (_entries.TryDequeue(out _))
                Interlocked.Decrement(ref _count);
        }
    }

    public string GetAllEntries()
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== Trace Logs");

        foreach (var entry in _entries)
        {
            sb.AppendLine($"[{entry.Timestamp:HH:mm:ss.fff}] [T{entry.ThreadId}] {entry.Message}");
        }

        return sb.ToString();
    }

    public void Clear()
    {
        while (_entries.TryDequeue(out _)) { }
    }

    private class TraceEntry
    {
        public DateTime Timestamp { get; set; }
        public string? Message { get; set; }
        public int ThreadId { get; set; }
    }
}
public static class TraceManager
{
    private static InMemoryTraceListener? _listener;

    public static void Initialize()
    {
        // Enable if we don't want debugger output...
        // Trace.Listeners.Clear();

        _listener = new InMemoryTraceListener(maxEntries: 25000);
        Trace.Listeners.Add(_listener);

        Trace.WriteLine("TraceManager initialized");
    }

    public static string GetAllTraceLogs()
    {
        return _listener?.GetAllEntries() ?? "Trace logging not initialized";
    }

    public static void ClearTraceLogs()
    {
        _listener?.Clear();
    }
}

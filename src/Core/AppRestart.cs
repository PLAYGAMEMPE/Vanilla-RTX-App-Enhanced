using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Restarts the whole app process. Microsoft.Windows.AppLifecycle.AppInstance.Restart is the
/// Windows App SDK's own restart API - unlike the classic UWP AppInstance, it's explicitly
/// designed to work for both packaged and unpackaged apps. It's synchronous and reports
/// failure via its return value rather than throwing (a successful restart replaces this
/// process before the call can return), so both paths - an exception, or a returned failure
/// reason - fall back to manually relaunching the same executable and exiting, which works
/// unconditionally. Kept as an async-returning Task for callers already inside async
/// methods, even though there's no actual async work left to await.
/// </summary>
public static class AppRestart
{
    public static Task RestartApp()
    {
        try
        {
            // If this actually succeeds, the process is replaced and this line never
            // returns - so getting past it at all means the restart failed.
            Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
        }
        catch
        {
            // Fall through to the manual fallback below.
        }

        FallbackRestart();
        return Task.CompletedTask;
    }

    private static void FallbackRestart()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                Process.Start(exePath);
        }
        catch
        {
            // Nothing more we can safely do - the caller already told the user
            // a restart is needed, so they can relaunch it themselves.
        }
        finally
        {
            Environment.Exit(0);
        }
    }
}

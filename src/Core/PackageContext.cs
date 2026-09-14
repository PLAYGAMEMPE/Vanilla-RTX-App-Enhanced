namespace Vanilla_RTX_App.Core;

/// <summary>
/// Whether this process has MSIX package identity. True for the sideloaded dev-mode
/// build; false for the portable unpackaged .exe (see Properties\PublishProfiles\win-x64.pubxml).
/// A handful of APIs - some of ours, some in third-party libraries like WinUIEx's window
/// position persistence - only work with package identity and can fail in ways that
/// aren't reliably catchable at the call site (e.g. from a deferred internal event
/// handler), so those call sites should check this and skip the packaged-only behavior
/// entirely rather than try/catching around it.
/// </summary>
public static class PackageContext
{
    public static readonly bool IsPackaged = DetectIsPackaged();

    private static bool DetectIsPackaged()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Fixes a real "works on my PC, not on a clean one" bug in the portable (self-contained,
/// unpackaged, single-file) build: Windows App SDK's native components -
/// Microsoft.WindowsAppRuntime.dll, DWriteCore.dll, and the rest - are classic Win32/C++
/// binaries that dynamically link against the Visual C++ 2015-2022 Redistributable
/// (vcruntime140.dll, vcruntime140_1.dll, msvcp140.dll, etc.). .NET's own self-contained
/// deployment only covers the managed CLR side (which genuinely needs nothing preinstalled,
/// by design since .NET Core 3.0) - it has no reason to know about that separate native
/// dependency, so it never bundles it. A dev machine almost always already has these DLLs in
/// System32 (Visual Studio, games, countless other installers put them there), which is
/// exactly why a build can run perfectly where it was produced and then fail silently - no
/// window, no error - on a machine that never had a reason to install the redistributable.
///
/// The publish profile (Portable-x64.pubxml) bundles verified, Microsoft-signed copies of
/// those DLLs into the single-file .exe under Runtimes\win-x64\vcredist\ - confirmed, by
/// actually launching the built .exe and inspecting its %TEMP% self-extraction folder, to
/// really get extracted there at runtime. But every attempt to make the single-file
/// bundler's own path computation place them at the extraction root instead (where Windows'
/// default DLL search order would find them) got silently overridden back to that nested
/// path - reproduced with three different MSBuild metadata techniques. Rather than keep
/// fighting an SDK internal that doesn't behave as documented in this version, this takes
/// the deterministic path: register that nested folder as an extra DLL search directory via
/// the Win32 APIs made for exactly this ("app-local" native dependency redistribution),
/// before anything else in the process gets a chance to load a native module.
/// </summary>
internal static class NativeDependencyBootstrap
{
    // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS - restricts the
    // process to the safe, explicit search order (app directory, System32, paths added via
    // AddDllDirectory) instead of the legacy behavior that also searches the current
    // directory, and is a prerequisite for AddDllDirectory to affect *implicit* dependency
    // resolution (not just explicit LoadLibraryEx calls) for modules loaded afterwards.
    private const int LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const int LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;

    /// <summary>
    /// A module initializer runs once, automatically, the moment this assembly is loaded -
    /// before the WinUI/Windows App SDK-generated Main and before anything else in the app
    /// gets a chance to trigger loading a native component. That ordering is the entire
    /// point: the search path has to be registered before Microsoft.WindowsAppRuntime.dll
    /// (or any of its native siblings) is loaded, or it's too late for their own implicit
    /// vcruntime140.dll/msvcp140.dll dependency resolution to see it.
    /// </summary>
    [ModuleInitializer]
    internal static void EnsureBundledVCRedistIsOnTheSearchPath()
    {
        try
        {
            if (!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS))
                return;

            // AppContext.BaseDirectory correctly resolves to the single-file app's
            // self-extraction folder at this point (not the original packed .exe's own
            // folder) - this is standard .NET single-file behavior, not specific to this
            // fix - so this finds the bundled copies wherever the SDK actually extracted
            // them to on this machine.
            var vcredistDir = Path.Combine(AppContext.BaseDirectory, "Runtimes", "win-x64", "vcredist");
            if (Directory.Exists(vcredistDir))
                AddDllDirectory(vcredistDir);

            // Harmless, deliberately silent no-op on the MSIX-packaged dev build: that build
            // isn't single-file, so this folder never exists there, and the packaged Windows
            // App SDK framework dependency already brings its own correct native runtime.
        }
        catch
        {
            // Best-effort only. Worst case if this ever throws (it shouldn't - every API
            // here is available since Windows 8/Server 2012), the app falls back to
            // whatever the OS's default DLL search order already provides - i.e. exactly
            // the pre-fix behavior, not a new regression.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(int directoryFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}

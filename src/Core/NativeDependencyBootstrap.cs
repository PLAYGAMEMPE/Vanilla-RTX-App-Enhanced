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
/// the deterministic path: load the bundled runtime DLLs by absolute path before WinAppSDK
/// needs them. Loaded modules are resolved by base name for later native dependencies, and
/// this avoids changing the process-wide DLL search policy used by WinUI.
/// </summary>
internal static class NativeDependencyBootstrap
{
    private static readonly string[] VCRuntimeLibraries =
    [
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "vcruntime140_threads.dll",
        "msvcp140.dll",
        "msvcp140_1.dll",
        "msvcp140_2.dll",
        "msvcp140_atomic_wait.dll",
        "msvcp140_codecvt_ids.dll",
        "concrt140.dll",
        "vccorlib140.dll"
    ];

    /// <summary>
    /// A module initializer runs once, automatically, the moment this assembly is loaded -
    /// before the WinUI/Windows App SDK-generated Main and before anything else in the app
    /// gets a chance to trigger loading a native component. That ordering is the entire
    /// point: the app-local runtime has to be loaded before Microsoft.WindowsAppRuntime.dll
    /// (or any of its native siblings) resolves its implicit VC++ dependencies.
    /// </summary>
    [ModuleInitializer]
    internal static void PreloadBundledVCRedist()
    {
        try
        {
            var vcredistDir = Path.Combine(
                AppContext.BaseDirectory,
                "Runtimes",
                "win-x64",
                "vcredist");

            if (!Directory.Exists(vcredistDir))
                return;

            foreach (var libraryName in VCRuntimeLibraries)
            {
                var libraryPath = Path.Combine(vcredistDir, libraryName);
                if (File.Exists(libraryPath))
                    NativeLibrary.Load(libraryPath);
            }
        }
        catch (Exception ex)
        {
            ReportNativeRuntimeFailure(ex);
            throw;
        }
    }

    private static void ReportNativeRuntimeFailure(Exception exception)
    {
        const string fileName = "native_runtime_startup_error.txt";
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vanilla RTX App",
            "LocalState",
            fileName);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, exception.ToString());
            MessageBoxW(
                IntPtr.Zero,
                "No se pudo cargar el runtime nativo incluido.\n" +
                "The bundled native runtime could not be loaded.\n\n" +
                $"Diagnostico / diagnostic log:\n{logPath}",
                "Vanilla RTX App - Runtime error",
                0x00000010);
        }
        catch
        {
            // There is no safer fallback before WinUI itself has initialized.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);

}

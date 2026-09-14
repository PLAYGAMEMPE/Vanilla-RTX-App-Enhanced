using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vanilla_RTX_App.Core;

namespace Vanilla_RTX_App.Modules;

/// <summary>
/// Automatic, first-touch backup for whole resource pack folders, so anything this app does
/// that could destroy a user's tuning/customization work on a pack - Tuner overwriting
/// textures in place, PackUpdater deleting+replacing a pack to update it, or the Delete
/// button removing one outright - has a real "undo" path, not just a confirmation dialog.
///
/// Design goals, driven directly by bugs found auditing the existing BetterRTX/LUT backup
/// code before writing this:
/// - A backup is only ever considered "complete" once a marker file is written as the very
///   last step of a successful copy. BetterRTX's own backup used "does at least one file
///   exist" as its completeness check, which could permanently wedge an incomplete backup
///   (e.g. 1 of 4 files copied, then interrupted) with no way to ever finish it - this marker
///   approach means an interrupted copy is always retried from scratch next time, never
///   mistaken for done.
/// - One backup per pack, taken once, ever, kept forever (until the user explicitly restores
///   it, or wipes app data) - later calls for a pack that's already backed up are no-ops, so
///   the backup always reflects the pack's state from before this app ever touched it, not
///   whatever it looked like most recently.
/// - Keyed by the pack's folder name (not its manifest UUID) - resource_packs/
///   development_resource_packs folders are already required to have unique names by
///   Windows/Minecraft itself, and keying this way means backup/restore never has to parse
///   or trust a manifest.json that might itself be malformed.
/// </summary>
public static class PackBackupService
{
    private static readonly string BackupRoot = EnsureDir(Path.Combine(AppStorage.LocalFolderPath, "PackBackups"));
    private const string CompleteMarkerFileName = "__vanillartxapp_backup_complete.marker";

    // Guards against two overlapping calls for the exact same pack racing each other (e.g. a
    // pack somehow queued for both Tuner and a Delete in the same moment) - collisions across
    // different packs never contend since each gets its own key.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static string BackupFolderFor(string packFolderPath) =>
        Path.Combine(BackupRoot, SanitizeKey(Path.GetFileName(packFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));

    private static string SanitizeKey(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    /// <summary>True if a complete backup already exists for this pack folder.</summary>
    public static bool HasBackup(string packFolderPath)
    {
        try
        {
            var marker = Path.Combine(BackupFolderFor(packFolderPath), CompleteMarkerFileName);
            return File.Exists(marker);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Backs up the entire pack folder if (and only if) it hasn't been backed up before.
    /// Safe and cheap to call unconditionally right before any operation that could destroy
    /// or overwrite pack contents - Tuner, PackUpdater's delete-before-replace, and the
    /// Delete button all call this first. Returns true if a backup exists after this call
    /// (whether just created or already there), false only if backing up a not-yet-backed-up
    /// pack failed outright (in which case the caller should treat the pack as unprotected,
    /// but this never blocks the caller's own operation from proceeding - a missed backup is
    /// far better logged and reported than turned into a hard failure that stops the user
    /// from tuning/updating/deleting anything).
    /// </summary>
    public static async Task<bool> EnsureBackedUpAsync(string packFolderPath)
    {
        if (string.IsNullOrEmpty(packFolderPath) || !Directory.Exists(packFolderPath))
            return false;

        if (HasBackup(packFolderPath))
            return true;

        var gate = _locks.GetOrAdd(packFolderPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // Re-check now that we hold the lock - another caller may have just finished.
            if (HasBackup(packFolderPath))
                return true;

            var backupFolder = BackupFolderFor(packFolderPath);

            // Wipe any half-finished attempt from a previous crash/interruption before
            // retrying - the marker's absence already told us it wasn't complete, so
            // whatever partial content is sitting there isn't trustworthy either.
            if (Directory.Exists(backupFolder))
                await Task.Run(() => Directory.Delete(backupFolder, recursive: true));

            await Task.Run(() => CopyDirectory(packFolderPath, backupFolder));

            // Written last, on purpose - see the marker-based completeness design above.
            await File.WriteAllTextAsync(
                Path.Combine(backupFolder, CompleteMarkerFileName),
                $"Backed up {DateTime.Now:O} from: {packFolderPath}\nAutomatic backup - Vanilla RTX App. Safe to delete only if you no longer need to restore this pack's original files.");

            Trace.WriteLine($"[PackBackupService] Backed up '{packFolderPath}' -> '{backupFolder}'.");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackBackupService] Failed to back up '{packFolderPath}': {ex.Message}");
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Restores a pack from its backup, replacing whatever is currently at packFolderPath
    /// (deleted first, then the backup is copied back in full). Returns false without
    /// touching anything if there's no complete backup for this pack.
    /// </summary>
    public static async Task<bool> RestoreAsync(string packFolderPath)
    {
        if (!HasBackup(packFolderPath))
            return false;

        var gate = _locks.GetOrAdd(packFolderPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var backupFolder = BackupFolderFor(packFolderPath);

            if (Directory.Exists(packFolderPath))
                await Task.Run(() => Directory.Delete(packFolderPath, recursive: true));

            await Task.Run(() => CopyDirectory(backupFolder, packFolderPath));

            // The marker is an implementation detail of the backup store, not something
            // that belongs inside the live, restored pack folder.
            var restoredMarker = Path.Combine(packFolderPath, CompleteMarkerFileName);
            if (File.Exists(restoredMarker))
                File.Delete(restoredMarker);

            Trace.WriteLine($"[PackBackupService] Restored '{packFolderPath}' from backup.");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PackBackupService] Failed to restore '{packFolderPath}': {ex.Message}");
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var filePath in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(filePath));
            File.Copy(filePath, destFile, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }
}

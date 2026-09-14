using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Drop-in replacement for Windows.Storage.ApplicationData's LocalSettings / LocalFolder /
/// LocalCacheFolder / TemporaryFolder. Windows.Storage.ApplicationData.Current requires MSIX
/// package identity and throws when the app runs unpackaged (a portable .exe with no
/// installer) - this exists so the whole app's persisted settings and cache data work
/// identically whether it's sideloaded as MSIX (dev mode) or published as a standalone .exe
/// (see Properties\PublishProfiles\win-x64.pubxml), using the exact same code path in both.
///
/// Everything lives under %LocalAppData%\Vanilla RTX App\, mirroring the three folders a
/// packaged install would have gotten for free (LocalState/LocalCache/TempState), so this
/// reads the same as "where would this data have been" for anyone used to the packaged
/// layout.
///
/// <see cref="Values"/> mirrors ApplicationData.LocalSettings.Values' shape (an
/// object-typed key/value store with ContainsKey/Remove) closely enough that call sites
/// that used to say `ApplicationData.Current.LocalSettings.Values[...]` only need
/// `AppStorage.Values[...]` swapped in - including EnvironmentVariables.LoadSettings(), whose
/// Convert.ChangeType(storedValue, field.FieldType) call depends on getting back the exact
/// original CLR type (bool stays bool, int stays int, etc.), which is why values are
/// persisted to disk with an explicit type tag instead of as bare JSON.
/// </summary>
public static partial class AppStorage
{
    private const string AppFolderName = "Vanilla RTX App";

    private static readonly string RootFolder = EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName));

    /// <summary>Equivalent of ApplicationData.Current.LocalFolder.Path.</summary>
    public static string LocalFolderPath { get; } = EnsureDir(Path.Combine(RootFolder, "LocalState"));

    /// <summary>Equivalent of ApplicationData.Current.LocalCacheFolder.Path.</summary>
    public static string LocalCacheFolderPath { get; } = EnsureDir(Path.Combine(RootFolder, "LocalCache"));

    /// <summary>Equivalent of ApplicationData.Current.TemporaryFolder.Path.</summary>
    public static string TemporaryFolderPath { get; } = EnsureDir(Path.Combine(RootFolder, "TempState"));

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static readonly string SettingsFilePath = Path.Combine(RootFolder, "settings.json");
    private static readonly object Gate = new();
    private static Dictionary<string, object?>? _cache;

    private static Dictionary<string, object?> Store()
    {
        if (_cache != null) return _cache;
        lock (Gate)
        {
            if (_cache != null) return _cache;
            _cache = new Dictionary<string, object?>();
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var raw = JsonSerializer.Deserialize(
                        File.ReadAllText(SettingsFilePath),
                        SettingsJsonContext.Default.DictionaryStringTaggedValue);
                    if (raw != null)
                        foreach (var pair in raw)
                            _cache[pair.Key] = pair.Value.ToNative();
                }
            }
            catch
            {
                // Corrupt/unreadable settings file - start fresh rather than crash startup.
                _cache = new Dictionary<string, object?>();
            }
            return _cache;
        }
    }

    private static void Persist()
    {
        lock (Gate)
        {
            try
            {
                var tagged = new Dictionary<string, TaggedValue>();
                foreach (var pair in _cache!)
                    tagged[pair.Key] = TaggedValue.FromNative(pair.Value);
                File.WriteAllText(
                    SettingsFilePath,
                    JsonSerializer.Serialize(tagged, SettingsJsonContext.Default.DictionaryStringTaggedValue));
            }
            catch
            {
                // Best-effort persistence - a failed write shouldn't crash the app.
            }
        }
    }

    /// <summary>Object-typed key/value store, mirroring ApplicationData.LocalSettings.Values.</summary>
    public static readonly ValuesStore Values = new();

    /// <summary>
    /// A static class can't declare an indexer (C# indexers are always instance members),
    /// hence this being a plain sealed class exposed through the single AppStorage.Values
    /// instance above rather than being static itself.
    /// </summary>
    public sealed class ValuesStore
    {
        internal ValuesStore() { }

        public object? this[string key]
        {
            get { lock (Gate) return Store().TryGetValue(key, out var v) ? v : null; }
            set { lock (Gate) Store()[key] = value; Persist(); }
        }

        public bool ContainsKey(string key)
        {
            lock (Gate) return Store().ContainsKey(key);
        }

        public bool Remove(string key)
        {
            bool removed;
            lock (Gate) removed = Store().Remove(key);
            if (removed) Persist();
            return removed;
        }

        public IReadOnlyCollection<string> Keys
        {
            get { lock (Gate) return new List<string>(Store().Keys); }
        }

        /// <summary>Wipes every stored key. Used by the app's "hard reset" feature.</summary>
        public void Clear()
        {
            lock (Gate) Store().Clear();
            Persist();
        }
    }

    internal sealed class TaggedValue
    {
        public string? T { get; set; }
        public JsonElement V { get; set; }

        public static TaggedValue FromNative(object? value) => value switch
        {
            null => new TaggedValue { T = null },
            bool b => new TaggedValue { T = "bool", V = JsonSerializer.SerializeToElement(b, SettingsJsonContext.Default.Boolean) },
            int i => new TaggedValue { T = "int", V = JsonSerializer.SerializeToElement(i, SettingsJsonContext.Default.Int32) },
            long l => new TaggedValue { T = "long", V = JsonSerializer.SerializeToElement(l, SettingsJsonContext.Default.Int64) },
            double d => new TaggedValue { T = "double", V = JsonSerializer.SerializeToElement(d, SettingsJsonContext.Default.Double) },
            string s => new TaggedValue { T = "string", V = JsonSerializer.SerializeToElement(s, SettingsJsonContext.Default.String) },
            _ => new TaggedValue { T = "string", V = JsonSerializer.SerializeToElement(value.ToString() ?? string.Empty, SettingsJsonContext.Default.String) },
        };

        public object? ToNative() => T switch
        {
            "bool" => V.GetBoolean(),
            "int" => V.GetInt32(),
            "long" => V.GetInt64(),
            "double" => V.GetDouble(),
            "string" => V.GetString(),
            _ => null,
        };
    }

    // System.Text.Json's reflection-based overloads are annotated RequiresUnreferencedCode
    // (that's the IL2026 warning): they walk a type's members at runtime, which trimming can
    // remove. Handing every call a compile-time-generated JsonTypeInfo instead makes the
    // serialization here fully static - trim-safe and warning-free - without disabling the
    // analyzer. Only the handful of concrete shapes this file actually round-trips are
    // registered; nothing here is polymorphic or open-generic.
    [JsonSerializable(typeof(Dictionary<string, TaggedValue>))]
    [JsonSerializable(typeof(TaggedValue))]
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(long))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(string))]
    private partial class SettingsJsonContext : JsonSerializerContext { }
}

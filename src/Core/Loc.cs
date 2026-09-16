using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Vanilla_RTX_App.Core;

/// <summary>
/// Thin wrapper around WinUI3/UWP's resource system for reading strings out of the
/// .resw files under Strings/&lt;lang-tag&gt;/. One .resw "resource map" per logical
/// area of the app (MainWindow, BetterRTXManager, DLSSSwapper, LUTManager,
/// PackBrowser, PackUpdater, Alchitex, Shared) instead of one giant file, so
/// translators/maintainers can find and edit strings without wading through
/// thousands of unrelated keys, and so multiple people/tools can touch different
/// files without merge conflicts.
///
/// Usage: Loc.Get("SomeKey") reads from Resources.resw (the shared/common map).
/// Loc.Get("SomeKey", "MainWindow") reads from MainWindow.resw, etc.
/// Loc.Format("SomeKey", arg1, arg2) does the same then runs string.Format on the
/// result, for strings with {0}/{1} placeholders (interpolated strings become
/// format strings once localized, since the pieces need to reorder per language).
///
/// Uses Microsoft.Windows.ApplicationModel.Resources.ResourceManager (Windows App
/// SDK's own MRT-Core resource manager) rather than the classic
/// Windows.ApplicationModel.Resources.ResourceLoader - the classic WinRT type
/// requires MSIX package identity and, critically, fails via an unrecoverable
/// native crash rather than a catchable .NET exception when it doesn't have one,
/// which made it a hard crash the very first time an unpackaged (portable .exe)
/// build asked for a named resource map. The Windows App SDK's ResourceManager is
/// explicitly built to also work unpackaged (it loads resources.pri from next to
/// the executable directly, no package required), and additionally lets the
/// desired language be forced per lookup via a ResourceContext's QualifierValues -
/// which LanguageService uses instead of the (also package-identity-dependent)
/// Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride.
/// </summary>
public static class Loc
{
    private const string ResourceKeyTagPrefix = "loc:";
    private static readonly ResourceManager _manager = new();
    private static readonly Dictionary<string, ResourceMap?> _maps = new();

    private static ResourceMap? GetMap(string resourceMap)
    {
        if (!_maps.TryGetValue(resourceMap, out var map))
        {
            try
            {
                map = _manager.MainResourceMap.GetSubtree(resourceMap);
            }
            catch
            {
                map = null;
            }
            _maps[resourceMap] = map;
        }
        return map;
    }

    /// <summary>
    /// Reads a string by key from the given resource map (defaults to the shared
    /// "Resources" map). Returns the key itself if not found, so a missing
    /// translation shows up as an obviously-wrong string in the UI instead of
    /// silently blank text or a crash.
    /// </summary>
    public static string Get(string key, string resourceMap = "Resources")
    {
        var map = GetMap(resourceMap);
        if (map == null) return key;

        try
        {
            var context = _manager.CreateResourceContext();
            context.QualifierValues["Language"] = LanguageService.CurrentLanguageTag;

            var candidate = map.GetValue(key, context);
            var value = candidate?.ValueAsString;
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch
        {
            return key;
        }
    }

    /// <summary>
    /// Same as Get, then string.Format(...) with the given arguments. The .resw
    /// entry should contain {0}, {1}, etc. placeholders.
    /// </summary>
    public static string Format(string key, string resourceMap, params object[] args) =>
        string.Format(Get(key, resourceMap), args);

    /// <summary>Format() against the shared "Resources" map.</summary>
    public static string Format(string key, params object[] args) =>
        string.Format(Get(key), args);

    /// <summary>
    /// Reads a dotted x:Uid-style key ("ElementName.Text", "ElementName.Content",
    /// etc.) by resolving it as two nested lookups - GetSubtree(elementName) then
    /// GetValue(propertySuffix) inside it - rather than passing the whole dotted
    /// string to a single GetValue call. The compiled resources.pri stores these
    /// x:Uid-driven entries as a real two-level hierarchy (matching how WinUI's own
    /// built-in x:Uid resolution looks them up internally), so a single
    /// GetValue("ElementName.Text") call misparses the dot and always fails
    /// (HRESULT 0x80073B17) even though the resource genuinely exists.
    /// Returns false (rather than falling back to the key itself, unlike Get above)
    /// so ApplyUidOverrides can tell "no translation for this" apart from "found an
    /// empty string" and knows not to overwrite whatever's already showing.
    /// </summary>
    private static bool TryGetUidProperty(string elementName, string property, string resourceMap, out string value)
    {
        value = string.Empty;
        var map = GetMap(resourceMap);
        if (map == null) return false;

        try
        {
            var elementSubtree = map.TryGetSubtree(elementName);
            if (elementSubtree == null) return false;

            var context = _manager.CreateResourceContext();
            context.QualifierValues["Language"] = LanguageService.CurrentLanguageTag;

            var candidate = elementSubtree.GetValue(property, context);
            var v = candidate?.ValueAsString;
            if (string.IsNullOrEmpty(v)) return false;

            value = v;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Walks the visual tree under root and re-applies Loc-driven translations to
    /// every element carrying an x:Uid, using the same (unpackaged-safe) lookup as
    /// Get/Format above - unlike Get/Format, WinUI's own built-in x:Uid resolution
    /// silently ignores LanguageService.CurrentLanguageTag when the app is running
    /// unpackaged (a portable .exe) and always falls back to whatever
    /// DefaultLanguage the project was built with (en-US here), since the API that
    /// would normally drive it - Windows.Globalization.ApplicationLanguages -
    /// requires package identity. Call this once per window, after its content is
    /// in the live visual tree (e.g. from its Loaded handler), passing that
    /// window's own resource map name.
    ///
    /// Keys off FrameworkElement.Name rather than x:Uid itself - unlike classic
    /// UWP, Microsoft.UI.Xaml's FrameworkElement doesn't expose Uid as a readable
    /// runtime property. Localized XAML elements therefore either keep x:Name and
    /// x:Uid aligned, or use AutomationProperties.AutomationId="loc:&lt;x:Uid&gt;"
    /// when adding x:Name would create an otherwise unnecessary generated field.
    /// The walk covers every property currently present in the app's dotted .resw
    /// entries, including attached tooltip/accessibility properties.
    /// </summary>
    public static void ApplyUidOverrides(DependencyObject? root, string resourceMap)
    {
        if (root == null) return;
        Walk(root, resourceMap);
    }

    /// <summary>
    /// Single-value counterpart to ApplyUidOverrides, for the one case it can't
    /// reach on its own: a Window's own Title. ApplyUidOverrides only walks a
    /// window's Content (a FrameworkElement) - the Window object itself is never
    /// part of that visual tree, so x:Uid="Window" + a "Window.Title" resw entry
    /// needs an explicit call instead. Falls back to whatever's passed in (the
    /// window's current Title, i.e. the XAML's own literal/x:Uid-resolved value)
    /// if no translation is found.
    /// </summary>
    public static string GetUidProperty(string elementName, string property, string resourceMap, string fallback) =>
        TryGetUidProperty(elementName, property, resourceMap, out var value) ? value : fallback;

    private static void Walk(DependencyObject node, string resourceMap)
    {
        if (node is FrameworkElement fe)
        {
            var name = GetResourceKey(fe);
            if (name != null)
            {
                if (fe is TextBlock tb && TryGetUidProperty(name, "Text", resourceMap, out var text))
                    tb.Text = text;

                if (fe is ContentControl cc && (cc.Content == null || cc.Content is string)
                    && TryGetUidProperty(name, "Content", resourceMap, out var content))
                    cc.Content = content;

                if (fe is ToggleSwitch toggle && (toggle.Header == null || toggle.Header is string)
                    && TryGetUidProperty(name, "Header", resourceMap, out var header))
                    toggle.Header = header;

                const string toolTipProperty =
                    "[using:Windows.UI.Xaml.Controls]ToolTipService.ToolTip";
                if (TryGetUidProperty(name, toolTipProperty, resourceMap, out var toolTip))
                    ToolTipService.SetToolTip(fe, toolTip);

                const string automationNameProperty =
                    "[using:Windows.UI.Xaml.Automation]AutomationProperties.Name";
                if (TryGetUidProperty(name, automationNameProperty, resourceMap, out var automationName))
                    AutomationProperties.SetName(fe, automationName);
            }
        }

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            Walk(VisualTreeHelper.GetChild(node, i), resourceMap);
    }

    private static string? GetResourceKey(FrameworkElement element)
    {
        if (!string.IsNullOrEmpty(element.Name))
            return element.Name;

        var automationId = AutomationProperties.GetAutomationId(element);
        if (!string.IsNullOrEmpty(automationId)
            && automationId.StartsWith(ResourceKeyTagPrefix, StringComparison.Ordinal)
            && automationId.Length > ResourceKeyTagPrefix.Length)
        {
            return automationId[ResourceKeyTagPrefix.Length..];
        }

        return null;
    }
}

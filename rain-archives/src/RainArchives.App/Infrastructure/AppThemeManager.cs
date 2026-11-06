using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RainArchives.App.Infrastructure;

internal enum AppThemePreference
{
    System,
    Light,
    Dark,
}

internal enum EffectiveAppTheme
{
    Light,
    Dark,
    HighContrast,
}

internal sealed class AppThemeManager : IDisposable
{
    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private readonly ResourceDictionary _resources;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _readSystemUsesLightTheme;
    private readonly Func<bool> _readHighContrast;
    private readonly Func<EffectiveAppTheme, ResourceDictionary> _loadPalette;
    private readonly Action<IntPtr, bool> _applyWindowChrome;
    private readonly object _windowChromeGate = new();
    private readonly ConditionalWeakTable<Window, WindowChromeAttachment>
        _windowChromeAttachments = new();
    private readonly HashSet<WindowChromeAttachment> _activeWindowChromeAttachments = [];
    private bool _isInitialized;
    private bool _isDisposed;

    internal AppThemeManager(
        Application application,
        Func<bool>? readSystemUsesLightTheme = null,
        Func<bool>? readHighContrast = null,
        Func<EffectiveAppTheme, ResourceDictionary>? loadPalette = null,
        Action<IntPtr, bool>? applyWindowChrome = null)
        : this(
            (application ?? throw new ArgumentNullException(nameof(application))).Resources,
            application.Dispatcher,
            readSystemUsesLightTheme,
            readHighContrast,
            loadPalette,
            applyWindowChrome)
    {
    }

    internal AppThemeManager(
        ResourceDictionary resources,
        Dispatcher dispatcher,
        Func<bool>? readSystemUsesLightTheme = null,
        Func<bool>? readHighContrast = null,
        Func<EffectiveAppTheme, ResourceDictionary>? loadPalette = null,
        Action<IntPtr, bool>? applyWindowChrome = null)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _readSystemUsesLightTheme = readSystemUsesLightTheme ?? ReadSystemUsesLightTheme;
        _readHighContrast = readHighContrast ?? (() => SystemParameters.HighContrast);
        _loadPalette = loadPalette ?? LoadPalette;
        _applyWindowChrome = applyWindowChrome ?? ApplyDwmWindowChrome;
    }

    internal event EventHandler? ThemeChanged;

    internal AppThemePreference Preference { get; private set; } = AppThemePreference.System;

    internal EffectiveAppTheme EffectiveTheme { get; private set; } = EffectiveAppTheme.Light;

    internal int AttachedWindowCount
    {
        get
        {
            lock (_windowChromeGate)
            {
                return _activeWindowChromeAttachments.Count;
            }
        }
    }

    internal void Initialize()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        ApplyCurrentPreference();
    }

    internal void SetPreference(AppThemePreference preference)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        Preference = preference;
        ApplyCurrentPreference();
    }

    internal void ApplyWindowChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!OperatingSystem.IsWindows() || PresentationSource.FromVisual(window) is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _applyWindowChrome(handle, EffectiveTheme == EffectiveAppTheme.Dark);
    }

    internal IDisposable AttachWindowChrome(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        WindowChromeAttachment attachment;
        lock (_windowChromeGate)
        {
            if (_windowChromeAttachments.TryGetValue(window, out attachment!))
            {
                return attachment;
            }

            attachment = new WindowChromeAttachment(this, window);
            _windowChromeAttachments.Add(window, attachment);
            _activeWindowChromeAttachments.Add(attachment);
        }

        attachment.ApplyIfReady();
        return attachment;
    }

    internal static EffectiveAppTheme ResolveEffectiveTheme(
        AppThemePreference preference,
        bool highContrast,
        bool systemUsesLightTheme)
    {
        if (highContrast)
        {
            return EffectiveAppTheme.HighContrast;
        }

        return preference switch
        {
            AppThemePreference.Light => EffectiveAppTheme.Light,
            AppThemePreference.Dark => EffectiveAppTheme.Dark,
            AppThemePreference.System => systemUsesLightTheme
                ? EffectiveAppTheme.Light
                : EffectiveAppTheme.Dark,
            _ => throw new ArgumentOutOfRangeException(nameof(preference)),
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        WindowChromeAttachment[] attachments;
        lock (_windowChromeGate)
        {
            _isDisposed = true;
            attachments = [.. _activeWindowChromeAttachments];
        }

        if (_isInitialized)
        {
            SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        }

        foreach (var attachment in attachments)
        {
            attachment.Dispose();
        }
    }

    private void ApplyCurrentPreference()
    {
        var effectiveTheme = ResolveEffectiveTheme(
            Preference,
            _readHighContrast(),
            _readSystemUsesLightTheme());
        var palette = _loadPalette(effectiveTheme);
        var dictionaries = _resources.MergedDictionaries;
        var paletteIndex = FindPaletteIndex(dictionaries);
        if (paletteIndex < 0)
        {
            dictionaries.Insert(0, palette);
        }
        else
        {
            dictionaries[paletteIndex] = palette;
        }

        EffectiveTheme = effectiveTheme;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        QueueSystemRefresh();

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        QueueSystemRefresh();

    private void QueueSystemRefresh()
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () =>
            {
                if (!_isDisposed)
                {
                    ApplyCurrentPreference();
                }
            });
    }

    private static int FindPaletteIndex(IList<ResourceDictionary> dictionaries)
    {
        for (var index = 0; index < dictionaries.Count; index++)
        {
            var source = dictionaries[index].Source?.OriginalString;
            if (dictionaries[index].Contains("ThemePaletteMarker")
                || (source is not null
                    && source.Contains("Themes/", StringComparison.OrdinalIgnoreCase)
                    && (source.EndsWith("/Light.xaml", StringComparison.OrdinalIgnoreCase)
                        || source.EndsWith("/Dark.xaml", StringComparison.OrdinalIgnoreCase)
                        || source.EndsWith("/HighContrast.xaml", StringComparison.OrdinalIgnoreCase))))
            {
                return index;
            }
        }

        return -1;
    }

    private static ResourceDictionary LoadPalette(EffectiveAppTheme effectiveTheme)
    {
        var paletteName = effectiveTheme switch
        {
            EffectiveAppTheme.Light => "Light",
            EffectiveAppTheme.Dark => "Dark",
            EffectiveAppTheme.HighContrast => "HighContrast",
            _ => throw new InvalidOperationException("The effective application theme is not supported."),
        };
        return new ResourceDictionary
        {
            Source = new Uri(
                $"/RainArchives;component/Themes/{paletteName}.xaml",
                UriKind.Relative),
        };
    }

    private static bool ReadSystemUsesLightTheme()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath, writable: false);
            return key?.GetValue(AppsUseLightThemeValue) is not int value || value != 0;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (SecurityException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private void DetachWindowChrome(WindowChromeAttachment attachment)
    {
        lock (_windowChromeGate)
        {
            _activeWindowChromeAttachments.Remove(attachment);
            if (attachment.TryGetWindow(out var window)
                && _windowChromeAttachments.TryGetValue(window, out var current)
                && ReferenceEquals(current, attachment))
            {
                _windowChromeAttachments.Remove(window);
            }
        }
    }

    private static void ApplyDwmWindowChrome(IntPtr handle, bool useDarkTheme)
    {
        var useDarkChrome = useDarkTheme ? 1 : 0;
        if (DwmSetWindowAttribute(
                handle,
                UseImmersiveDarkMode,
                ref useDarkChrome,
                sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(
                handle,
                UseImmersiveDarkModeBefore20H1,
                ref useDarkChrome,
                sizeof(int));
        }
    }

    private sealed class WindowChromeAttachment : IDisposable
    {
        private readonly WeakReference<Window> _window;
        private AppThemeManager? _manager;
        private int _isDisposed;

        internal WindowChromeAttachment(AppThemeManager manager, Window window)
        {
            _manager = manager;
            _window = new WeakReference<Window>(window);
            window.SourceInitialized += Window_SourceInitialized;
            window.Closed += Window_Closed;
            manager.ThemeChanged += Manager_ThemeChanged;
        }

        internal void ApplyIfReady()
        {
            if (Volatile.Read(ref _isDisposed) == 0
                && _manager is { } manager
                && _window.TryGetTarget(out var window))
            {
                manager.ApplyWindowChrome(window);
            }
        }

        internal bool TryGetWindow(out Window window) => _window.TryGetTarget(out window!);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            var manager = Interlocked.Exchange(ref _manager, null);
            if (_window.TryGetTarget(out var window))
            {
                window.SourceInitialized -= Window_SourceInitialized;
                window.Closed -= Window_Closed;
            }

            if (manager is not null)
            {
                manager.ThemeChanged -= Manager_ThemeChanged;
                manager.DetachWindowChrome(this);
            }
        }

        private void Window_SourceInitialized(object? sender, EventArgs e) => ApplyIfReady();

        private void Window_Closed(object? sender, EventArgs e) => Dispose();

        private void Manager_ThemeChanged(object? sender, EventArgs e)
        {
            if (_window.TryGetTarget(out _))
            {
                ApplyIfReady();
                return;
            }

            Dispose();
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}

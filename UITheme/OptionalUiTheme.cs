using System;
using System.Reflection;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Y4NGZUpgrades.UITheme
{
    internal enum HudColorPreset
    {
        Default,
        Orange,
        Green,
        Blue
    }

    internal enum UiColorTheme
    {
        Orange,
        Green,
        Blue
    }

    /// <summary>
    /// Upgrades-owned UI palette facade. It mirrors Y4NGZUI's selected palette through reflection,
    /// retains Contracted's delegating facade as a legacy fallback, and otherwise supplies the same
    /// orange defaults. This keeps every Upgrades UI usable without placing either presentation
    /// assembly in this project's reference table.
    /// </summary>
    internal static class UiTheme
    {
        private static readonly string[] ExternalTypeNames =
        {
            "Y4NGZUI.UiTheme, Y4NGZUI",
            "Y4NGZCompany.Experience.UITheme.UiTheme, Y4NGZCompany",
        };

        private static readonly OptionalTypeResolver ExternalThemeResolver =
            new OptionalTypeResolver(
                ExternalTypeNames,
                typeName => Type.GetType(typeName, throwOnError: false));

        private static Type _externalType;
        private static EventInfo _externalThemeChanged;
        private static Delegate _externalThemeHandler;
        private static Func<int, bool> _protectedSlotResolver;
        private static bool _initialized;

        private static readonly PaletteData Orange = PaletteData.FromHue(0.065f, 0.88f, 0.10f, 0.94f);

        internal static event Action<HudColorPreset> ThemeChanged;

        internal static Func<int, bool> ProtectedSlotResolver
        {
            get => _protectedSlotResolver;
            set
            {
                _protectedSlotResolver = value;
                WriteExternalProtectedSlotResolver(value);
            }
        }

        internal static HudColorPreset CurrentPreset
        {
            get
            {
                object value = ReadExternalProperty("CurrentPreset");
                if (value != null)
                {
                    try
                    {
                        int raw = Convert.ToInt32(value);
                        if (raw >= (int)HudColorPreset.Default && raw <= (int)HudColorPreset.Blue)
                            return (HudColorPreset)raw;
                    }
                    catch
                    {
                    }
                }

                return HudColorPreset.Default;
            }
        }

        internal static UiColorTheme CurrentTheme
        {
            get
            {
                switch (CurrentPreset)
                {
                    case HudColorPreset.Blue: return UiColorTheme.Blue;
                    case HudColorPreset.Green: return UiColorTheme.Green;
                    default: return UiColorTheme.Orange;
                }
            }
        }

        internal static Color Accent => ReadExternalColor("Accent", Orange.Accent);
        internal static Color Dim => ReadExternalColor("Dim", Orange.Dim);
        internal static Color Faint => ReadExternalColor("Faint", Orange.Faint);
        internal static Color Panel => ReadExternalColor("Panel", Orange.Panel);
        internal static Color Muted => ReadExternalColor("Muted", Orange.Muted);
        internal static Color Warning => ReadExternalColor("Warning", Orange.Warning);
        internal static Color Danger => ReadExternalColor("Danger", Orange.Danger);

        internal static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;
            ResolveExternalTheme();
            SubscribeToExternalTheme();
            WriteExternalProtectedSlotResolver(_protectedSlotResolver);
        }

        internal static void Shutdown()
        {
            if (_externalThemeChanged != null && _externalThemeHandler != null)
            {
                try { _externalThemeChanged.RemoveEventHandler(null, _externalThemeHandler); }
                catch { }
            }

            WriteExternalProtectedSlotResolver(null);
            _externalThemeChanged = null;
            _externalThemeHandler = null;
            _externalType = null;
            ExternalThemeResolver.Reset();
            ThemeChanged = null;
            _initialized = false;
        }

        internal static void Refresh()
        {
            ResolveExternalTheme();
            MethodInfo refresh = _externalType?.GetMethod(
                "Refresh", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (refresh != null)
            {
                try
                {
                    refresh.Invoke(null, null);
                    if (_externalThemeHandler != null)
                        return;
                }
                catch
                {
                }
            }

            RaiseThemeChanged();
        }

        private static void ResolveExternalTheme()
        {
            _externalType = ExternalThemeResolver.Resolve();
        }

        private static void SubscribeToExternalTheme()
        {
            if (_externalType == null || _externalThemeHandler != null)
                return;

            try
            {
                _externalThemeChanged = _externalType.GetEvent(
                    "ThemeChanged", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Type handlerType = _externalThemeChanged?.EventHandlerType;
                Type[] genericArguments = handlerType?.GetGenericArguments();
                if (handlerType == null || genericArguments == null || genericArguments.Length != 1)
                    return;

                MethodInfo callback = typeof(UiTheme).GetMethod(
                    nameof(OnExternalThemeChanged), BindingFlags.Static | BindingFlags.NonPublic)
                    ?.MakeGenericMethod(genericArguments[0]);
                if (callback == null)
                    return;

                _externalThemeHandler = Delegate.CreateDelegate(handlerType, callback);
                _externalThemeChanged.AddEventHandler(null, _externalThemeHandler);
            }
            catch
            {
                _externalThemeChanged = null;
                _externalThemeHandler = null;
            }
        }

        private static void OnExternalThemeChanged<T>(T ignored)
        {
            RaiseThemeChanged();
        }

        private static void RaiseThemeChanged()
        {
            Action<HudColorPreset> handlers = ThemeChanged;
            if (handlers == null)
                return;

            HudColorPreset preset = CurrentPreset;
            foreach (Delegate callback in handlers.GetInvocationList())
            {
                try { ((Action<HudColorPreset>)callback)(preset); }
                catch (Exception ex) { Debug.LogWarning("[Y4NGZUpgrades] UI theme listener failed: " + ex.Message); }
            }
        }

        private static object ReadExternalProperty(string name)
        {
            ResolveExternalTheme();
            try
            {
                return _externalType?.GetProperty(
                    name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        private static Color ReadExternalColor(string name, Color fallback)
        {
            object value = ReadExternalProperty(name);
            return value is Color color ? color : fallback;
        }

        private static void WriteExternalProtectedSlotResolver(Func<int, bool> resolver)
        {
            ResolveExternalTheme();
            try
            {
                FieldInfo field = _externalType?.GetField(
                    "ProtectedSlotResolver", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && (resolver == null || field.FieldType.IsInstanceOfType(resolver)))
                    field.SetValue(null, resolver);
            }
            catch
            {
            }
        }

        private readonly struct PaletteData
        {
            internal readonly Color Accent;
            internal readonly Color Dim;
            internal readonly Color Faint;
            internal readonly Color Panel;
            internal readonly Color Muted;
            internal readonly Color Warning;
            internal readonly Color Danger;

            private PaletteData(Color accent, Color dim, Color faint, Color panel, Color muted, Color warning)
            {
                Accent = accent;
                Dim = dim;
                Faint = faint;
                Panel = panel;
                Muted = muted;
                Warning = warning;
                Danger = new Color(0.86f, 0.13f, 0.10f, 1f);
            }

            internal static PaletteData FromHue(float hue, float dimValue, float panelValue, float saturation)
            {
                Color accent = FromHsv(hue, saturation, 1f, 1f);
                Color dim = FromHsv(hue, Mathf.Max(0.38f, saturation - 0.22f), dimValue, 0.88f);
                Color faint = FromHsv(hue, Mathf.Max(0.42f, saturation - 0.14f), 0.54f, 0.24f);
                Color panel = FromHsv(hue, Mathf.Max(0.56f, saturation - 0.10f), panelValue, 0.88f);
                Color muted = FromHsv(hue, 0.28f, 0.62f, 0.85f);
                Color warning = FromHsv(Mathf.Repeat(hue * 0.30f, 1f), 0.94f, 1f, 1f);
                return new PaletteData(accent, dim, faint, panel, muted, warning);
            }

            private static Color FromHsv(float hue, float saturation, float value, float alpha)
            {
                Color color = Color.HSVToRGB(hue, saturation, value);
                color.a = alpha;
                return color;
            }
        }
    }

    /// <summary>Upgrades-owned gameplay overlay visibility gate with an optional report probe.</summary>
    internal static class GameplayUiVisibility
    {
        private const string GameplaySceneName = "SampleSceneRelay";
        private const string ReportTypeName =
            "Y4NGZCompany.Contracts._Shared.ContractPerformanceReportUi, Y4NGZCompany";

        private static readonly string[] VisibilityTypeNames =
        {
            "Y4NGZUI.GameplayUiVisibility, Y4NGZUI",
            "Y4NGZCompany.Experience.UITheme.GameplayUiVisibility, Y4NGZCompany",
        };

        private static bool _initialized;
        private static bool _lastPublishedVisibility;
        private static PropertyInfo _reportActiveProperty;
        private static bool _reportProbeResolved;
        private static bool _externalVisibilityResolved;
        private static PropertyInfo _externalVisibilityProperty;

        internal static event Action<bool> VisibilityChanged;
        internal static bool IsVisible => ComputeVisibility();

        internal static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            _lastPublishedVisibility = ComputeVisibility();
        }

        internal static void Shutdown()
        {
            if (!_initialized)
                return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            VisibilityChanged = null;
            _initialized = false;
            _externalVisibilityResolved = false;
            _externalVisibilityProperty = null;
        }

        internal static void PublishCurrentState(bool force = false)
        {
            bool visible = ComputeVisibility();
            if (!force && visible == _lastPublishedVisibility)
                return;
            _lastPublishedVisibility = visible;
            Action<bool> handlers = VisibilityChanged;
            if (handlers == null)
                return;
            foreach (Delegate callback in handlers.GetInvocationList())
            {
                try { ((Action<bool>)callback)(visible); }
                catch { }
            }
        }

        private static bool ComputeVisibility()
        {
            if (TryReadExternalVisibility(out bool externalVisible))
                return externalVisible;

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || scene.name != GameplaySceneName)
                return false;

            PlayerControllerB player = GameNetworkManager.Instance?.localPlayerController
                                       ?? StartOfRound.Instance?.localPlayerController;
            if (player != null && player.quickMenuManager != null && player.quickMenuManager.isMenuOpen)
                return false;
            if (PlayerIsUsingTerminal(player))
                return false;
            return !IsRoundEndReportActive();
        }

        private static bool TryReadExternalVisibility(out bool visible)
        {
            if (!_externalVisibilityResolved)
            {
                _externalVisibilityResolved = true;
                for (int i = 0; i < VisibilityTypeNames.Length && _externalVisibilityProperty == null; i++)
                {
                    Type type = Type.GetType(VisibilityTypeNames[i], throwOnError: false);
                    _externalVisibilityProperty = type?.GetProperty(
                        "IsVisible", BindingFlags.Static | BindingFlags.Public);
                }
            }

            try
            {
                if (_externalVisibilityProperty?.GetValue(null) is bool value)
                {
                    visible = value;
                    return true;
                }
            }
            catch { }

            visible = false;
            return false;
        }

        private static bool PlayerIsUsingTerminal(PlayerControllerB player)
        {
            if (player == null)
                return false;
            try
            {
                FieldInfo field = typeof(PlayerControllerB).GetField(
                    "inTerminalMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field != null && field.GetValue(player) is bool active && active;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRoundEndReportActive()
        {
            if (!_reportProbeResolved)
            {
                _reportProbeResolved = true;
                Type type = OptionalPluginCapabilities.Contracted
                    ? Type.GetType(ReportTypeName, throwOnError: false)
                    : null;
                _reportActiveProperty = type?.GetProperty(
                    "IsRoundEndPresentationActive",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }

            try
            {
                return _reportActiveProperty != null
                       && _reportActiveProperty.GetValue(null) is bool active
                       && active;
            }
            catch
            {
                return false;
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            PublishCurrentState(force: true);
        }
    }

    internal static class GameplayHudMotion
    {
        private static readonly string[] MotionTypeNames =
        {
            "Y4NGZUI.GameplayHudMotion, Y4NGZUI",
            "Y4NGZCompany.Experience.UITheme.GameplayHudMotion, Y4NGZCompany",
        };

        private static bool _initialized;
        private static Type _externalType;
        private static PropertyInfo _externalCurrentOffset;
        private static EventInfo _externalMotionSampled;
        private static Delegate _externalMotionHandler;

        internal static Vector2 CurrentOffset { get; private set; }
        internal static event Action<Vector2> MotionSampled;

        internal static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;
            ResolveExternalMotion();
            SceneManager.sceneLoaded += OnSceneLoaded;
            Publish(ReadExternalOffset());
        }

        internal static void Shutdown()
        {
            if (!_initialized)
                return;

            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_externalMotionSampled != null && _externalMotionHandler != null)
            {
                try { _externalMotionSampled.RemoveEventHandler(null, _externalMotionHandler); }
                catch { }
            }

            _externalType = null;
            _externalCurrentOffset = null;
            _externalMotionSampled = null;
            _externalMotionHandler = null;
            CurrentOffset = Vector2.zero;
            MotionSampled = null;
            _initialized = false;
        }

        private static void ResolveExternalMotion()
        {
            for (int i = 0; i < MotionTypeNames.Length && _externalType == null; i++)
                _externalType = Type.GetType(MotionTypeNames[i], throwOnError: false);
            if (_externalType == null)
                return;

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public;
            _externalCurrentOffset = _externalType.GetProperty("CurrentOffset", flags);
            _externalMotionSampled = _externalType.GetEvent("MotionSampled", flags);
            if (_externalMotionSampled == null)
                return;

            try
            {
                MethodInfo callback = typeof(GameplayHudMotion).GetMethod(
                    nameof(OnExternalMotionSampled), BindingFlags.Static | BindingFlags.NonPublic);
                _externalMotionHandler = Delegate.CreateDelegate(
                    _externalMotionSampled.EventHandlerType, callback);
                _externalMotionSampled.AddEventHandler(null, _externalMotionHandler);
            }
            catch
            {
                _externalMotionSampled = null;
                _externalMotionHandler = null;
            }
        }

        private static Vector2 ReadExternalOffset()
        {
            try
            {
                return _externalCurrentOffset?.GetValue(null) is Vector2 offset
                    ? offset
                    : Vector2.zero;
            }
            catch
            {
                return Vector2.zero;
            }
        }

        private static void OnExternalMotionSampled(Vector2 offset)
        {
            Publish(offset);
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Publish(_externalType != null ? ReadExternalOffset() : Vector2.zero);
        }

        private static void Publish(Vector2 offset)
        {
            CurrentOffset = offset;
            Action<Vector2> handlers = MotionSampled;
            if (handlers == null)
                return;
            foreach (Delegate callback in handlers.GetInvocationList())
            {
                try { ((Action<Vector2>)callback)(offset); }
                catch { }
            }
        }
    }
}

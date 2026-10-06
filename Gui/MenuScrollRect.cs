using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Y4NGZUpgrades.Gui;

// Only the Player Menu uses this component. Never retune the shared EventSystem:
// vanilla and other mods must retain their own input settings.
internal sealed class MenuScrollRect : ScrollRect
{
    private static readonly PropertyInfo ModuleTickScale =
        typeof(InputSystemUIInputModule).GetProperty("scrollDeltaPerTick");
    // Resolved once per process. MenuController nulls PurchaseMenu statics, so this lives here.
    private static readonly bool RuntimeNormalizesWheel = DetectRuntimeNormalizesWheel();
    private int _restoreGeneration;
    private int _inputRevision;
    private int _loggedScrolls;

    internal void ObserveScrollbar()
    {
        if (verticalScrollbar != null)
            verticalScrollbar.onValueChanged.AddListener(_ => _inputRevision++);
    }

    public override void OnScroll(PointerEventData eventData)
    {
        if (eventData == null || eventData.used || !IsActive() || !vertical) return;
        _inputRevision++;
        float scale = GetEventUnitsPerTick(EventSystem.current?.currentInputModule);
        Vector2 original = eventData.scrollDelta;
        float ticks = MenuScrollMath.Ticks(original.y, scale);
        eventData.scrollDelta = new Vector2(0f, ticks);
        float before = content != null ? content.anchoredPosition.y : 0f;
        try { base.OnScroll(eventData); }
        finally { eventData.scrollDelta = original; }
        eventData.Use();
        if (_loggedScrolls++ < 4)
            Plugin.ExtendedLogging($"[PMenu/Scroll] module={EventSystem.current?.currentInputModule?.GetType().Name} " +
                $"raw={Mouse.current?.scroll.ReadValue().y} delivered={original.y} unitsPerTick={scale} ticks={ticks} " +
                $"movement={(content != null ? content.anchoredPosition.y - before : 0f)} " +
                $"overflow={(content != null && viewport != null ? content.rect.height - viewport.rect.height : 0f)}");
    }

    private static float GetEventUnitsPerTick(BaseInputModule module)
    {
        // The legacy module already delivers one unit per notch.
        if (!(module is InputSystemUIInputModule)) return 1f;
        float moduleScale = 1f;
        try
        {
            if (ModuleTickScale != null)
                moduleScale = Convert.ToSingle(ModuleTickScale.GetValue(module));
        }
        catch (Exception ex) { Plugin.ExtendedLogging("[PMenu/Scroll] input scale unavailable: " + ex.Message); }
        bool windowsRange = Application.platform == RuntimePlatform.WindowsPlayer ||
                            Application.platform == RuntimePlatform.WindowsEditor;
        return MenuScrollMath.UnitsPerTick(moduleScale, RuntimeNormalizesWheel, windowsRange);
    }

    // Input System 1.14 (LC's build) compiles scrollWheelDeltaPerTick as a literal 1f and never
    // divides by the platform range. A non-literal member means the runtime normalizes the wheel.
    private static bool DetectRuntimeNormalizesWheel()
    {
        const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            FieldInfo field = typeof(InputSystem).GetField("scrollWheelDeltaPerTick", Flags);
            if (field != null) return !field.IsLiteral;
            return typeof(InputSystem).GetProperty("scrollWheelDeltaPerTick", Flags) != null;
        }
        catch (Exception ex)
        {
            Plugin.ExtendedLogging("[PMenu/Scroll] wheel normalization probe failed: " + ex.Message);
            return false;
        }
    }

    public override void OnBeginDrag(PointerEventData eventData)
    {
        _inputRevision++;
        base.OnBeginDrag(eventData);
    }

    internal void CancelPendingRestore() { _restoreGeneration++; }

    internal void RestorePosition(float position)
    {
        StopMovement();
        verticalNormalizedPosition = Mathf.Clamp01(position);
        int revision = _inputRevision;
        int generation = ++_restoreGeneration;
        if (isActiveAndEnabled) StartCoroutine(RestoreAfterLayout(position, generation, revision));
    }

    private IEnumerator RestoreAfterLayout(float position, int generation, int revision)
    {
        yield return null;
        yield return null;
        if (generation != _restoreGeneration || revision != _inputRevision || !isActiveAndEnabled)
            yield break;
        StopMovement();
        verticalNormalizedPosition = Mathf.Clamp01(position);
    }
}

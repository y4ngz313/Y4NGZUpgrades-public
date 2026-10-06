using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace Y4NGZUpgrades.Gui;

// Attached to the menu root canvas. Handles open/close animation, cursor
// restoration, and destruction of the root. Restored from decompiled Y4NGZMenu.dll.
public class MenuController : MonoBehaviour
{
    private GameObject    _root;
    private CanvasGroup   _canvasGroup;
    private RectTransform _panelRt;
    private bool          _closing;
    private int           _frameOpenedOn = -1;

    // Set while our menu is open so QuickMenuManager.OpenQuickMenu patch can
    // suppress the vanilla pause menu without affecting other callers.
    public static bool IsOpen;

    private const float OpenDuration  = 0.18f;
    private const float CloseDuration = 0.12f;

    public void Init(GameObject root)
    {
        _root        = root;
        _canvasGroup = _root.GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = _root.AddComponent<CanvasGroup>();

        var panel = _root.transform.Find("Panel");
        if (panel != null) _panelRt = panel.GetComponent<RectTransform>();

        IsOpen         = true;
        _frameOpenedOn = Time.frameCount;
        StartCoroutine(AnimateOpen());
    }

    // ESC closes the menu and suppresses the vanilla pause menu for the same
    // press (via the QuickMenuManager.OpenQuickMenu Harmony prefix).
    // P also closes - so pressing P twice toggles the menu, matching how
    // inventory-style menus usually work. Bug #7.
    private void Update()
    {
        if (_closing) return;

        // Guard: Keybinds.PurchaseMenu.performed fires and creates this
        // MonoBehaviour in the same frame as the first Update call.
        // wasPressedThisFrame for P is still true that frame, so without this
        // guard the menu would open and immediately close on the same press.
        if (Time.frameCount == _frameOpenedOn) return;

        // Close is a dedicated InputUtils action. The player-menu toggle only closes when a text
        // field does not own the press, matching the old P behavior while allowing both controls
        // to be rebound from the normal controls screen.
        bool typing = false;
        try { typing = PurchaseMenu.IsTextInputFocused(); }
        catch (System.Exception e) { LogKeyboardFaultOnce(e); }

        IngameKeybinds keybinds = Plugin.Keybinds;
        ApplyMenuScale();
        PurchaseMenu.TickAppearanceState();
        if (UpgradeInput.WasPressed(keybinds?.PlayerMenuClose)
            || (!typing && UpgradeInput.WasPressed(keybinds?.PurchaseMenu)))
        {
            CloseMenu();
            return;
        }

        // Everything else the controls do inside the menu (#255). Gated on this component
        // existing at all, which is exactly "the menu is open", so chat and terminal input never
        // share a press with it. A persistent fault is logged once instead of flooding Update.
        try { PurchaseMenu.HandleKeyboardNavigation(); }
        catch (System.Exception e) { LogKeyboardFaultOnce(e); }
    }

    // Keyed by exception text rather than count so a second, different fault is
    // still reported. Static: the set outlives any single menu instance.
    private static readonly System.Collections.Generic.HashSet<string> _loggedKeyboardFaults =
        new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

    private static void LogKeyboardFaultOnce(System.Exception e)
    {
        if (e == null) return;
        string signature = e.GetType().FullName + "|" + e.Message + "|" + e.StackTrace;
        if (!_loggedKeyboardFaults.Add(signature)) return;
        Plugin.CustomLogger?.LogError($"[PMenu] Keyboard navigation failed: {e}");
    }

    // Safety net for #237: AnimateClose clears the vanilla latch only after its
    // yield loop, so a close interrupted before then (death, teleport, scene
    // unload destroying the root) would strand quickMenuManager.isMenuOpen and
    // leave vanilla input blocked. This runs on every teardown route; the
    // in-coroutine clear stays, and clearing an already-clear flag is a no-op.
    private void OnDisable()
    {
        try { ReleaseVanillaMenuLatch(); } catch { }
        IsOpen = false;
    }

    private static void ReleaseVanillaMenuLatch()
    {
        var player = StartOfRound.Instance?.localPlayerController;
        if (player != null && player.quickMenuManager != null)
            player.quickMenuManager.isMenuOpen = false;
        // #237: paired with the Push in PurchaseMenu.OpenCursor. Both clear routes
        // (AnimateClose and the OnDisable safety net) reach here, and Pop is a no-op
        // unless the latch is actually held, so a double clear cannot underflow.
        CompanyInputWatchdogLatch.Pop();
    }

    // The open/close fade used to re-assert localScale = one on every pass,
    // which would flatten the player's Menu Scale setting the moment the menu
    // opened. The scale is owned by PurchaseMenu.BuildRoot; these calls just
    // keep it correct if the config changed between builds.
    private void ApplyMenuScale()
    {
        if (_panelRt != null)
            _panelRt.localScale = Vector3.one * PurchaseMenu.FittedMenuScale(_root.GetComponent<RectTransform>());
    }

    private IEnumerator AnimateOpen()
    {
        _canvasGroup.alpha = 0f;
        ApplyMenuScale();

        float t = 0f;
        while (t < OpenDuration && !(Plugin.ReduceMenuMotion?.Value ?? false))
        {
            t += Time.unscaledDeltaTime;
            float n    = Mathf.Clamp01(t / OpenDuration);
            float ease = 1f - Mathf.Pow(1f - n, 3f);  // ease-out cubic

            _canvasGroup.alpha = ease;
            yield return null;
        }

        _canvasGroup.alpha = 1f;
        ApplyMenuScale();
    }

    public void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        // Owned here rather than on the X button so ESC and P close with the
        // same sound, and so no path can play it twice.
        MenuAudio.PlayExit();
        StartCoroutine(AnimateClose());
    }

    private IEnumerator AnimateClose()
    {
        float startAlpha = _canvasGroup != null ? _canvasGroup.alpha : 1f;

        float t = 0f;
        while (t < CloseDuration && !(Plugin.ReduceMenuMotion?.Value ?? false))
        {
            t += Time.unscaledDeltaTime;
            float n    = Mathf.Clamp01(t / CloseDuration);
            float ease = n * n * n;  // ease-in cubic

            if (_canvasGroup != null) _canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, ease);
            yield return null;
        }

        try
        {
            ReleaseVanillaMenuLatch();
            Cursor.visible   = false;
            Cursor.lockState = CursorLockMode.Locked;
        }
        catch { }

        IsOpen = false;
        // If TooManyEmotes' preview rig was running for our PLAYER COSMETICS
        // emote tab, halt it so the camera doesn't keep rendering after close.
        try { typeof(PurchaseMenu)
                .GetMethod("StopEmotePreview", BindingFlags.Static | BindingFlags.NonPublic)
                ?.Invoke(null, null); } catch { }
        // Hide the persistent employee preview rig without destroying the
        // render texture or clone; the next open reuses the same pipeline.
        try { typeof(PurchaseMenu)
                .GetMethod("HideEmployeePreview", BindingFlags.Static | BindingFlags.NonPublic)
                ?.Invoke(null, null); } catch { }
        // The keyboard focus list is `readonly` and so invisible to the
        // reflective sweep below; it holds Buttons on the root about to be
        // destroyed, so it has to be emptied explicitly.
        try { PurchaseMenu.ClearKeyboardFocus(); } catch { }
        ClearPurchaseMenuStatics();
        if (_root != null) Object.Destroy(_root);
    }

    // Null out static UnityObject fields AND the object-typed selection
    // trackers on PurchaseMenu so the next open rebuilds from scratch
    // rather than pointing at destroyed GOs or stale emote selections.
    private static readonly string[] _objectFieldsToClear = new[]
    {
        "_selectedEmoteForPurchase",
        "_previewingEmote",
        "_selectedCosmeticForPurchase",
        "_selectedCosmeticLabel",
        "_selectedCosmeticTypeLabel",
    };

    private static readonly string[] _previewFieldsToPreserve = new[]
    {
        "_employeePreviewTexture",
        "_employeePreviewRoot",
        "_employeePreviewCamera",
        "_employeePreviewTicker",
        "_employeePreviewModel",
        "_previewBodyMesh",
        "_previewSourceMesh",
        "_previewSourcePlayer",
        "_previewSpineRoot",
        // ModelReplacementAPI preview state. These pair with the bool
        // _previewUsesReplacement, which is not an Object field and so is never
        // cleared here; nulling only half the pair would leave the ticker
        // believing it mirrors a replacement it can no longer identify, and it
        // would stop noticing suits swapped while the menu was closed.
        "_previewReplacementSource",
        "_previewReplacementRejected",
    };

    private static bool ShouldPreservePurchaseMenuStatic(string fieldName)
    {
        for (int i = 0; i < _previewFieldsToPreserve.Length; i++)
        {
            if (fieldName == _previewFieldsToPreserve[i])
                return true;
        }

        return false;
    }

    // Reflective, by field type and name, so a static field added to
    // PurchaseMenu is covered without touching this file - but only if it is
    // writable. `readonly` collections are skipped: PurchaseMenu's section
    // registry (`Sections`) is immutable and must survive, and its parallel
    // `_sectionVisuals` cache is refilled wholesale by BuildModeRail on the next
    // build, with every consumer treating a destroyed Button as absent.
    private static void ClearPurchaseMenuStatics()
    {
        var fields = typeof(PurchaseMenu).GetFields(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (var f in fields)
        {
            if (f.IsLiteral || f.IsInitOnly) continue;
            if (ShouldPreservePurchaseMenuStatic(f.Name)) continue;
            if (typeof(Object).IsAssignableFrom(f.FieldType))
            {
                f.SetValue(null, null);
                continue;
            }
            for (int i = 0; i < _objectFieldsToClear.Length; i++)
            {
                if (f.Name == _objectFieldsToClear[i])
                {
                    f.SetValue(null, null);
                    break;
                }
            }
        }
    }
}

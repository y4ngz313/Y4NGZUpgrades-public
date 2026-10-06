using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using Y4NGZInteractions.InteractionAnimationApi;

namespace Y4NGZUpgrades.Patches;

/// <summary>
/// Resumes only a just-created, caller-owned Interactions presentation at an accepted fist
/// punch's elapsed progress. Interactions currently exposes trigger parameters but no public
/// state-time continuation operation. This bridge deliberately reaches only the new handle's
/// presenter animator; it never writes a player's normal animator or interaction it does not own.
/// </summary>
internal static class NativeFistsInteractionResumeBridge
{
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    private static readonly FieldInfo CoordinatorField = typeof(LCInteractionAnimationAPI)
        .GetField("coordinator", StaticPrivate);
    private static FieldInfo activeSessionsField;
    private static FieldInfo presenterField;
    private static FieldInfo bodyAnimatorField;
    private static FieldInfo viewmodelAnimatorField;
    private static FieldInfo fullBodyLayerField;
    private static Type coordinatorType;
    private static Type sessionType;
    private static Type presenterType;

    /// <summary>
    /// Applies the normalized time after the new controller has consumed its normal punch trigger.
    /// The caller schedules this for the following frame, so current/next state inspection sees
    /// the authored punch state rather than the controller entry state.
    /// </summary>
    internal static bool TryResumePunchAt(
        InteractionAnimationHandle handle,
        float normalizedProgress,
        string expectedPunchStateName)
    {
        if (!handle.IsValid || string.IsNullOrWhiteSpace(expectedPunchStateName) ||
            !LCInteractionAnimationAPI.IsInteractionActive(handle))
            return false;

        try
        {
            object presenter = GetPresenter(handle);
            Animator animator = GetAnimator(presenter);
            if (animator == null || animator.runtimeAnimatorController == null)
                return false;

            int layer = GetPresentationLayer(presenter, animator);
            if (layer < 0 || layer >= animator.layerCount)
                return false;

            // The trigger is set through the public API first. Evaluate that authored transition,
            // then seek its current/next state on this one owned animator only.
            animator.Update(0f);
            AnimatorStateInfo state = animator.IsInTransition(layer)
                ? animator.GetNextAnimatorStateInfo(layer)
                : animator.GetCurrentAnimatorStateInfo(layer);
            // A next-frame schedule is not proof that this controller consumed PunchLeft/Right.
            // Reject Enter/Idle or the opposite hand rather than seeking an unrelated state.
            if (state.fullPathHash == 0 ||
                state.shortNameHash != Animator.StringToHash(expectedPunchStateName))
                return false;

            animator.Play(state.fullPathHash, layer, Mathf.Clamp01(normalizedProgress));
            animator.Update(0f);
            return true;
        }
        catch
        {
            // Interactions can be upgraded independently. A changed private implementation merely
            // leaves the new presentation at its authored entry frame; gameplay is unaffected.
            return false;
        }
    }

    private static object GetPresenter(InteractionAnimationHandle handle)
    {
        object coordinator = CoordinatorField?.GetValue(null);
        if (coordinator == null)
            return null;
        Type nextCoordinatorType = coordinator.GetType();
        if (coordinatorType != nextCoordinatorType || activeSessionsField == null)
        {
            coordinatorType = nextCoordinatorType;
            activeSessionsField = coordinatorType.GetField("activeSessions", InstancePrivate);
        }
        if (!(activeSessionsField?.GetValue(coordinator) is IDictionary sessions) ||
            !sessions.Contains(handle))
        {
            return null;
        }

        object session = sessions[handle];
        if (session == null)
            return null;
        Type nextSessionType = session.GetType();
        if (sessionType != nextSessionType || presenterField == null)
        {
            sessionType = nextSessionType;
            presenterField = sessionType.GetField("presenter", InstancePrivate);
        }
        return presenterField?.GetValue(session);
    }

    private static Animator GetAnimator(object presenter)
    {
        if (presenter == null)
            return null;
        Type nextPresenterType = presenter.GetType();
        if (presenterType != nextPresenterType)
        {
            presenterType = nextPresenterType;
            bodyAnimatorField = presenterType.GetField("bodyAnimator", InstancePrivate);
            viewmodelAnimatorField = presenterType.GetField("viewmodelAnimator", InstancePrivate);
            fullBodyLayerField = presenterType.GetField("fullBodyLayerIndex", InstancePrivate);
        }
        return bodyAnimatorField?.GetValue(presenter) as Animator ??
               viewmodelAnimatorField?.GetValue(presenter) as Animator;
    }

    private static int GetPresentationLayer(object presenter, Animator animator)
    {
        object value = fullBodyLayerField?.GetValue(presenter);
        if (value is int layer && layer >= 0 && layer < animator.layerCount)
            return layer;
        return 0;
    }
}

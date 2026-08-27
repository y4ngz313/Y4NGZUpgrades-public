using System.Collections;
using UnityEngine;
using Y4NGZUpgrades.Interactive;
using Y4NGZUpgrades.UITheme;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Mod-owned coroutine host and per-frame janitor driver.
    ///
    /// PROVEN NEEDED 2026-08-12 (#211): the grenade throw ran its coroutine on
    /// <c>PlayerControllerB</c> and put every restoration step in the iterator's <c>finally</c>.
    /// An abandoned coroutine — StopCoroutine, StopAllCoroutines, GameObject deactivation,
    /// component destruction — never runs that <c>finally</c>, so a single abandonment left the
    /// shared <c>DiscardHeldObject</c> action disabled and the held item pocketed for the rest of
    /// the session, with no log line to say so.
    ///
    /// This host survives scene loads and player teardown, so a routine started here keeps
    /// running, and its Update drives the janitors that force cleanup even when the routine is
    /// gone. It self-recreates if something destroys it, and it deliberately owns no state of its
    /// own: every tick target keeps its own registry and stays idempotent.
    /// </summary>
    internal static class Y4NGZPersistentRunner
    {
        private static Y4NGZPersistentRunnerBehaviour _runner;

        /// <summary>
        /// The mod-owned session host. Components and coroutines that must survive scene changes
        /// belong here rather than on BepInEx_Manager, whose lifetime depends on
        /// HideManagerGameObject (#313).
        /// </summary>
        internal static MonoBehaviour Host
        {
            get
            {
                Ensure();
                return _runner;
            }
        }

        /// <summary>Idempotent; safe to call from any thread-affine Unity context.</summary>
        internal static void Ensure()
        {
            if (_runner != null)
                return;

            GameObject host = new GameObject("Y4NGZUpgradesPersistentRunner");
            Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            _runner = host.AddComponent<Y4NGZPersistentRunnerBehaviour>();
        }

        /// <summary>
        /// Starts <paramref name="routine"/> on the persistent host. Returns null only if the host
        /// could not be created, which callers must treat as "run the cleanup path now".
        /// </summary>
        internal static Coroutine Run(IEnumerator routine)
        {
            if (routine == null)
                return null;

            Ensure();
            return _runner != null ? _runner.StartCoroutine(routine) : null;
        }

        /// <summary>Stops a routine started by <see cref="Run"/>. Null-safe and idempotent.</summary>
        internal static void Stop(Coroutine routine)
        {
            if (routine == null || _runner == null)
                return;

            _runner.StopCoroutine(routine);
        }

        private sealed class Y4NGZPersistentRunnerBehaviour : MonoBehaviour
        {
            private void Update()
            {
                // Moved off PlayerControllerB.Update (#211): the local player's Update is exactly
                // what stops running when a stow strands, so the janitor has to live somewhere the
                // player cannot take down with it.
                HeldItemStowService.Tick();
                // The standalone facade computes pause, terminal and optional Contracted-report
                // visibility live. Publish transitions here so overlays that use the shared event
                // (not only the live property) stay correct without Contracted's Harmony patches.
                GameplayUiVisibility.PublishCurrentState();
                // GrenadeThrowPatches.TickLocalThrowJanitor moved to
                // BetterArmory.ArmoryPersistentRunner with the grenade (#266).
            }
        }
    }
}

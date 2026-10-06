using System;
using System.Collections.Generic;
using System.Reflection;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// #392: hands Y4NGZCompany's performance report the XP each contract act earned, so the
    /// line Company already writes for that act ("> Restored the breaker box") carries "(N XP)"
    /// instead of the whole amount landing on this plugin's generic contract note.
    ///
    /// Optional and reflection-bound: no compile-time reference, and with Company absent - or
    /// exposing a protocol other than the one this was written against - nothing is recorded,
    /// <see cref="Record"/> returns 0, and the whole bucket stays on this plugin's own line
    /// exactly as before. Upgrades remains the payout authority; this is presentation only.
    /// The sink SUMS repeated calls, so the caller must call this from a path the round latch
    /// admits once per round.
    /// </summary>
    internal static class CompanyReportXpBridge
    {
        private const string SinkTypeName = "Y4NGZCompany.Contracts._Shared.PerformanceReportXpAmounts, Y4NGZCompany";
        private const int SupportedProtocolVersion = 1;

        private static bool _noteOwnerResolved;
        private static MethodInfo _recordSources;
        private static MethodInfo _applyNotes;
        internal static bool HasNoteOwner { get { ResolveNoteOwner(); return _recordSources != null && _applyNotes != null; } }

        private static void ResolveNoteOwner()
        {
            if (_noteOwnerResolved) return;
            _noteOwnerResolved = true;
            try
            {
                Type ledger = Type.GetType("Y4NGZCompany.Contracts._Shared.PerformanceReportXpLedger, Y4NGZCompany", false);
                if (!(ledger?.GetField("ProtocolVersion")?.GetRawConstantValue() is int version) || version != 1) return;
                _recordSources = ledger.GetMethod("RecordSources", new[] { typeof(int), typeof(string[]), typeof(string[]), typeof(int[]), typeof(int) });
                Type presenter = Type.GetType("Y4NGZCompany.Contracts._Shared.PerformanceReportXpPresenter, Y4NGZCompany", false);
                _applyNotes = presenter?.GetMethod("TryApplyNotes", new[] { typeof(object), typeof(int) });
            }
            catch { _recordSources = null; _applyNotes = null; }
        }

        internal static void RecordSources(int playerIndex, IReadOnlyList<ProgressionReportSource> sources, int total, int sequence)
        {
            if (OptionalPluginCapabilities.BetterExp || !HasNoteOwner || sources == null) return;
            try
            {
                var keys = new string[sources.Count];
                var labels = new string[sources.Count];
                var amounts = new int[sources.Count];
                for (int i = 0; i < sources.Count; i++)
                {
                    keys[i] = sources[i].Key;
                    labels[i] = ProgressionReportNotePlan.DescribeSource(sources[i], sequence);
                    amounts[i] = sources[i].Amount;
                }
                _recordSources.Invoke(null, new object[] { playerIndex, keys, labels, amounts, total });
            }
            catch (Exception ex) { Plugin.Log?.LogWarning("[Progression] Could not record Company XP notes: " + ex.Message); }
        }

        internal static bool TryApplyNotes(object hud, int playerIndex)
        {
            if (!HasNoteOwner) return false;
            try { return _applyNotes.Invoke(null, new object[] { hud, playerIndex }) is bool applied && applied; }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Progression] Company XP notes unavailable; using standalone layout: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// #458: whether this peer's report takes part in the crew share - Company is present on
        /// protocol 1 with both seams and BetterEXP does not own the surface. Gates both the send
        /// and the recording of a received share.
        /// </summary>
        internal static bool CanShare => !OptionalPluginCapabilities.BetterExp && HasNoteOwner && Resolve() != null;

        /// <summary>
        /// #458: records another player's shared report for its own slot through the same two
        /// seams the local round uses. Never called for the local slot.
        /// </summary>
        internal static bool RecordShare(ProgressionReportShare share)
        {
            if (share == null || !CanShare)
                return false;
            try
            {
                return share.RecordInto(
                    (slot, keys, labels, amounts, total) =>
                        _recordSources.Invoke(null, new object[] { slot, keys, labels, amounts, total }) is bool accepted && accepted,
                    (slot, key, amount) => _recordAmount.Invoke(null, new object[] { slot, key, amount }));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("[Progression] Could not record a crewmate's report XP: " + ex.Message);
                return false;
            }
        }

        private static bool _resolved;
        private static MethodInfo _recordAmount;
        private static bool _loggedFailure;

        /// <summary>
        /// Records every positive line for the local player's report slot. Returns the XP
        /// actually handed over, which is what the caller subtracts from its own contract line.
        /// #1201: <paramref name="crewWideRecorded"/> is the part of it handed over under a
        /// <c>crew.</c> key - crew awards the report hides rather than decorates.
        /// </summary>
        internal static int Record(int playerIndex, IReadOnlyList<KeyValuePair<string, int>> lines, out int crewWideRecorded)
        {
            crewWideRecorded = 0;
            if (playerIndex < 0 || lines == null || lines.Count == 0)
                return 0;

            MethodInfo record = Resolve();
            if (record == null)
                return 0;

            int recorded = 0;
            try
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    string key = lines[i].Key;
                    int amount = lines[i].Value;
                    if (string.IsNullOrWhiteSpace(key) || amount <= 0)
                        continue;

                    record.Invoke(null, new object[] { playerIndex, key, amount });
                    recorded += amount;
                    if (ProgressionReportLineKeys.IsCrewWideKey(key))
                        crewWideRecorded += amount;
                }
            }
            catch (Exception ex)
            {
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Plugin.Log?.LogWarning(
                        $"[Progression] Could not hand report XP amounts to Y4NGZCompany: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return recorded;
        }

        private static MethodInfo Resolve()
        {
            if (_resolved)
                return _recordAmount;

            _resolved = true;
            try
            {
                Type sink = Type.GetType(SinkTypeName, throwOnError: false);
                if (sink == null)
                    return null;

                FieldInfo version = sink.GetField("ProtocolVersion", BindingFlags.Public | BindingFlags.Static);
                object raw = version != null && version.IsLiteral ? version.GetRawConstantValue() : version?.GetValue(null);
                if (!(raw is int protocol) || protocol != SupportedProtocolVersion)
                {
                    Plugin.Log?.LogWarning(
                        "[Progression] Y4NGZCompany's report XP sink speaks protocol "
                        + $"{(raw is int p ? p.ToString() : "<none>")}, this plugin expects {SupportedProtocolVersion}; "
                        + "contract XP stays on the Upgrades note.");
                    return null;
                }

                _recordAmount = sink.GetMethod(
                    "RecordAmount",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(int), typeof(string), typeof(int) },
                    null);
            }
            catch
            {
                _recordAmount = null;
            }

            return _recordAmount;
        }
    }
}

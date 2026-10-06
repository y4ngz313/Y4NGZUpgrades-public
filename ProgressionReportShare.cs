using System;
using System.Collections.Generic;
using System.Text;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// #458: one player's performance-report XP presentation, shared so every crew member's row
    /// shows that player's own top lines. Presentation only: the receiver records it into Company's
    /// report seams for the sender's slot and pays nothing.
    ///
    /// It carries exactly what the sender handed its own report: the ledger snapshot (source keys,
    /// labels, amounts, round total) and the line-key amounts given to
    /// <c>PerformanceReportXpAmounts</c>, <c>crew.</c> keys included so a crew award stays hidden on
    /// every screen. The round identity is the host-issued CCTV round epoch and level token.
    ///
    /// Wire form (little-endian, version 1): version byte, epoch u64, level token u64, slot i32,
    /// total i32, source count byte, then per source key/label/amount, line count byte, then per
    /// line key/amount. A string is a byte length and that many UTF-8 bytes. Unity-free, so the
    /// round trip and every cap are checked in ProgressionChecks.
    /// </summary>
    internal sealed class ProgressionReportShare
    {
        internal const byte WireVersion = 1;
        /// <summary>The whole encoded payload; larger is dropped on send and on receive.</summary>
        internal const int MaxPayloadBytes = 2048;
        internal const int MaxSources = 32;
        internal const int MaxLines = 32;
        internal const int MaxKeyBytes = 64;
        /// <summary>Labels are truncated to this many characters on send and refused past it on receive.</summary>
        internal const int MaxLabelChars = 64;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal ulong Epoch { get; }
        internal ulong LevelToken { get; }
        internal int Slot { get; }
        internal int Total { get; }
        internal string[] SourceKeys { get; }
        internal string[] SourceLabels { get; }
        internal int[] SourceAmounts { get; }
        internal string[] LineKeys { get; }
        internal int[] LineAmounts { get; }

        internal ProgressionReportShare(
            ulong epoch,
            ulong levelToken,
            int slot,
            int total,
            string[] sourceKeys,
            string[] sourceLabels,
            int[] sourceAmounts,
            string[] lineKeys,
            int[] lineAmounts)
        {
            Epoch = epoch;
            LevelToken = levelToken;
            Slot = slot;
            Total = total;
            SourceKeys = sourceKeys ?? Array.Empty<string>();
            SourceLabels = sourceLabels ?? Array.Empty<string>();
            SourceAmounts = sourceAmounts ?? Array.Empty<int>();
            LineKeys = lineKeys ?? Array.Empty<string>();
            LineAmounts = lineAmounts ?? Array.Empty<int>();
        }

        /// <summary>
        /// The sender's view of its own finalized round: every positive source with the label its
        /// own report prints (truncated to <see cref="MaxLabelChars"/>), and every positive line
        /// amount it handed to Company.
        /// </summary>
        internal static ProgressionReportShare FromRound(
            ulong epoch,
            ulong levelToken,
            int slot,
            int total,
            IReadOnlyList<ProgressionReportSource> sources,
            int roundSequence,
            IReadOnlyList<KeyValuePair<string, int>> lines)
        {
            var sourceKeys = new List<string>();
            var sourceLabels = new List<string>();
            var sourceAmounts = new List<int>();
            if (sources != null)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    ProgressionReportSource source = sources[i];
                    if (source == null || source.Amount <= 0)
                        continue;
                    sourceKeys.Add(source.Key);
                    sourceLabels.Add(TruncateLabel(ProgressionReportNotePlan.DescribeSource(source, roundSequence)));
                    sourceAmounts.Add(source.Amount);
                }
            }

            var lineKeys = new List<string>();
            var lineAmounts = new List<int>();
            if (lines != null)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i].Key) || lines[i].Value <= 0)
                        continue;
                    lineKeys.Add(lines[i].Key);
                    lineAmounts.Add(lines[i].Value);
                }
            }

            return new ProgressionReportShare(epoch, levelToken, slot, Math.Max(0, total),
                sourceKeys.ToArray(), sourceLabels.ToArray(), sourceAmounts.ToArray(),
                lineKeys.ToArray(), lineAmounts.ToArray());
        }

        internal static string TruncateLabel(string label)
        {
            if (string.IsNullOrEmpty(label) || label.Length <= MaxLabelChars)
                return label ?? string.Empty;
            int length = MaxLabelChars;
            // Never split a surrogate pair: the half would not encode.
            if (char.IsHighSurrogate(label[length - 1]))
                length--;
            return label.Substring(0, length);
        }

        /// <summary>The encoded payload, or null when it breaks a cap (the message is then not sent).</summary>
        internal byte[] Encode()
        {
            if (Slot < 0 || Total < 0
                || SourceKeys.Length != SourceLabels.Length || SourceKeys.Length != SourceAmounts.Length
                || LineKeys.Length != LineAmounts.Length
                || SourceKeys.Length > MaxSources || LineKeys.Length > MaxLines)
            {
                return null;
            }

            var buffer = new List<byte>(256) { WireVersion };
            WriteUInt64(buffer, Epoch);
            WriteUInt64(buffer, LevelToken);
            WriteInt32(buffer, Slot);
            WriteInt32(buffer, Total);
            buffer.Add((byte)SourceKeys.Length);
            for (int i = 0; i < SourceKeys.Length; i++)
            {
                if (SourceLabels[i] == null || SourceLabels[i].Length > MaxLabelChars
                    || !WriteString(buffer, SourceKeys[i], MaxKeyBytes)
                    || !WriteString(buffer, SourceLabels[i], byte.MaxValue))
                {
                    return null;
                }
                WriteInt32(buffer, SourceAmounts[i]);
            }

            buffer.Add((byte)LineKeys.Length);
            for (int i = 0; i < LineKeys.Length; i++)
            {
                if (!WriteString(buffer, LineKeys[i], MaxKeyBytes))
                    return null;
                WriteInt32(buffer, LineAmounts[i]);
            }

            return buffer.Count > MaxPayloadBytes ? null : buffer.ToArray();
        }

        /// <summary>
        /// Parses a payload. False for anything oversized, truncated, trailing, of another wire
        /// version, or breaking a cap; never throws.
        /// </summary>
        internal static bool TryDecode(byte[] payload, out ProgressionReportShare share)
        {
            share = null;
            if (payload == null || payload.Length == 0 || payload.Length > MaxPayloadBytes)
                return false;

            try
            {
                int offset = 0;
                if (!ReadByte(payload, ref offset, out byte version) || version != WireVersion
                    || !ReadUInt64(payload, ref offset, out ulong epoch)
                    || !ReadUInt64(payload, ref offset, out ulong levelToken)
                    || !ReadInt32(payload, ref offset, out int slot)
                    || !ReadInt32(payload, ref offset, out int total)
                    || slot < 0 || total < 0
                    || !ReadByte(payload, ref offset, out byte sourceCount) || sourceCount > MaxSources)
                {
                    return false;
                }

                var sourceKeys = new string[sourceCount];
                var sourceLabels = new string[sourceCount];
                var sourceAmounts = new int[sourceCount];
                for (int i = 0; i < sourceCount; i++)
                {
                    if (!ReadString(payload, ref offset, MaxKeyBytes, out sourceKeys[i])
                        || !ReadString(payload, ref offset, byte.MaxValue, out sourceLabels[i])
                        || sourceLabels[i].Length > MaxLabelChars
                        || !ReadInt32(payload, ref offset, out sourceAmounts[i]))
                    {
                        return false;
                    }
                }

                if (!ReadByte(payload, ref offset, out byte lineCount) || lineCount > MaxLines)
                    return false;
                var lineKeys = new string[lineCount];
                var lineAmounts = new int[lineCount];
                for (int i = 0; i < lineCount; i++)
                {
                    if (!ReadString(payload, ref offset, MaxKeyBytes, out lineKeys[i])
                        || !ReadInt32(payload, ref offset, out lineAmounts[i]))
                    {
                        return false;
                    }
                }

                if (offset != payload.Length)
                    return false;

                share = new ProgressionReportShare(epoch, levelToken, slot, total,
                    sourceKeys, sourceLabels, sourceAmounts, lineKeys, lineAmounts);
                return true;
            }
            catch (ArgumentException)
            {
                // Invalid UTF-8 (DecoderFallbackException derives from ArgumentException).
                return false;
            }
        }

        /// <summary>
        /// Records this share for <see cref="Slot"/> through Company's two protocol-1 seams: the
        /// ledger snapshot first, and the line amounts only once the snapshot was accepted, so a
        /// rejected share decorates nothing. Returns whether the snapshot was accepted.
        /// </summary>
        internal bool RecordInto(
            Func<int, string[], string[], int[], int, bool> recordSources,
            Action<int, string, int> recordAmount)
        {
            if (recordSources == null || recordAmount == null
                || !recordSources(Slot, SourceKeys, SourceLabels, SourceAmounts, Total))
            {
                return false;
            }

            for (int i = 0; i < LineKeys.Length; i++)
                recordAmount(Slot, LineKeys[i], LineAmounts[i]);
            return true;
        }

        private static void WriteUInt64(List<byte> buffer, ulong value)
        {
            for (int i = 0; i < 8; i++)
                buffer.Add((byte)(value >> (8 * i)));
        }

        private static void WriteInt32(List<byte> buffer, int value)
        {
            for (int i = 0; i < 4; i++)
                buffer.Add((byte)((uint)value >> (8 * i)));
        }

        private static bool WriteString(List<byte> buffer, string value, int maxBytes)
        {
            if (value == null)
                return false;
            byte[] bytes = Utf8.GetBytes(value);
            if (bytes.Length > maxBytes || bytes.Length > byte.MaxValue)
                return false;
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
            return true;
        }

        private static bool ReadByte(byte[] payload, ref int offset, out byte value)
        {
            value = 0;
            if (offset >= payload.Length)
                return false;
            value = payload[offset++];
            return true;
        }

        private static bool ReadUInt64(byte[] payload, ref int offset, out ulong value)
        {
            value = 0;
            if (payload.Length - offset < 8)
                return false;
            for (int i = 0; i < 8; i++)
                value |= (ulong)payload[offset++] << (8 * i);
            return true;
        }

        private static bool ReadInt32(byte[] payload, ref int offset, out int value)
        {
            value = 0;
            if (payload.Length - offset < 4)
                return false;
            uint raw = 0;
            for (int i = 0; i < 4; i++)
                raw |= (uint)payload[offset++] << (8 * i);
            value = (int)raw;
            return true;
        }

        private static bool ReadString(byte[] payload, ref int offset, int maxBytes, out string value)
        {
            value = null;
            if (!ReadByte(payload, ref offset, out byte length) || length > maxBytes || payload.Length - offset < length)
                return false;
            value = Utf8.GetString(payload, offset, length);
            offset += length;
            return true;
        }
    }

    /// <summary>
    /// #458: which shared reports this peer has admitted. One per sender slot per round epoch - a
    /// second is a duplicate - never the viewer's own slot, never a round other than the current
    /// one, and never the round the report already closed on (the ship made ready to land, where
    /// Company clears its ledger): a late message from that round must not re-populate it.
    /// </summary>
    internal sealed class ProgressionReportShareInbox
    {
        private readonly HashSet<int> _slots = new HashSet<int>();
        private ulong _epoch;
        private ulong _levelToken;
        private ulong _closedEpoch;
        private ulong _closedLevelToken;

        /// <param name="isCurrentRound">
        /// The caller's check of (<paramref name="epoch"/>, <paramref name="levelToken"/>) against
        /// the host-issued round epoch this peer holds.
        /// </param>
        internal bool TryAdmit(ulong epoch, ulong levelToken, bool isCurrentRound, int slot, int localSlot)
        {
            if (!isCurrentRound || epoch == 0 || levelToken == 0 || slot < 0 || slot == localSlot
                || (epoch == _closedEpoch && levelToken == _closedLevelToken))
            {
                return false;
            }

            if (epoch != _epoch || levelToken != _levelToken)
            {
                _slots.Clear();
                _epoch = epoch;
                _levelToken = levelToken;
            }

            return _slots.Add(slot);
        }

        /// <summary>The report for this round is over; nothing more from it is admitted.</summary>
        internal void CloseRound(ulong epoch, ulong levelToken)
        {
            _closedEpoch = epoch;
            _closedLevelToken = levelToken;
            _slots.Clear();
        }

        internal void ResetSession()
        {
            _slots.Clear();
            _epoch = 0;
            _levelToken = 0;
            _closedEpoch = 0;
            _closedLevelToken = 0;
        }
    }
}

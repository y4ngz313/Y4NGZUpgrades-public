using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZUpgrades
{
    /// <summary>
    /// Answers "is this world position inside the facility interior?" with the nearest-AI-node
    /// test: the interior and the surface each carry their own node set
    /// (<c>RoundManager.insideAINodes</c> / <c>outsideAINodes</c>), so whichever set is closer is
    /// the volume the position sits in. A level without interior nodes has no interior.
    ///
    /// Sweeps call <see cref="CaptureNodes"/> once and then <see cref="IsInsideFactoryCaptured"/>
    /// per position, so node transforms are read once per sweep instead of once per item. The
    /// buffers are shared statics: main thread only, and a capture is valid until the next one.
    /// </summary>
    internal static class FacilityPositionQuery
    {
        private static readonly List<Vector3> InsideNodes = new List<Vector3>();
        private static readonly List<Vector3> OutsideNodes = new List<Vector3>();

        /// <summary>Single-position form: captures the current nodes and tests one position.</summary>
        internal static bool IsInsideFactory(Vector3 position)
        {
            CaptureNodes();
            return IsInsideFactoryCaptured(position);
        }

        /// <summary>
        /// Snapshots the current inside/outside AI node positions. Returns false when there is
        /// no interior node (no RoundManager, or a level such as the Company moon), in which case
        /// every captured test answers false.
        /// </summary>
        internal static bool CaptureNodes()
        {
            InsideNodes.Clear();
            OutsideNodes.Clear();
            RoundManager manager = RoundManager.Instance;
            if (manager == null)
                return false;

            CopyNodePositions(manager.insideAINodes, InsideNodes);
            CopyNodePositions(manager.outsideAINodes, OutsideNodes);
            return InsideNodes.Count > 0;
        }

        /// <summary>Tests a position against the last <see cref="CaptureNodes"/> snapshot.</summary>
        internal static bool IsInsideFactoryCaptured(Vector3 position)
        {
            float insideSqr = ClosestSqrDistance(InsideNodes, position);
            if (insideSqr == float.MaxValue)
                return false;

            return insideSqr < ClosestSqrDistance(OutsideNodes, position);
        }

        private static void CopyNodePositions(GameObject[] nodes, List<Vector3> into)
        {
            if (nodes == null)
                return;

            for (int i = 0; i < nodes.Length; i++)
            {
                GameObject node = nodes[i];
                if (node != null)
                    into.Add(node.transform.position);
            }
        }

        private static float ClosestSqrDistance(List<Vector3> nodes, Vector3 position)
        {
            float best = float.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                float sqr = (nodes[i] - position).sqrMagnitude;
                if (sqr < best)
                    best = sqr;
            }

            return best;
        }
    }
}

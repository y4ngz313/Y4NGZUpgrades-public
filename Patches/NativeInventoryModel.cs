namespace Y4NGZUpgrades.Patches
{
    /// <summary>
    /// Allocation-free rules shared by the runtime hotbar patches and model checks.
    /// Slot 50 remains vanilla's separate utility slot and is never represented here.
    /// </summary>
    internal static class NativeInventoryModel
    {
        internal const int VanillaSlotCount = 4;
        internal const int MaximumBonusSlots = 3;
        internal const int PhysicalSlotCount = VanillaSlotCount + MaximumBonusSlots;
        internal const int UtilitySlotIndex = 50;

        internal static int GetUnlockedSlotCount(int tier)
        {
            if (tier <= 0)
                return VanillaSlotCount;
            if (tier >= MaximumBonusSlots)
                return PhysicalSlotCount;
            return VanillaSlotCount + tier;
        }

        /// <summary>
        /// How many two-handed items may occupy the hotbar. Takes the ALLOWANCE rather than a
        /// rank: the unique-only Deeper Pockets variant (#435) sells the same three slot ranks
        /// without the second two-handed item, so the rank alone no longer answers this.
        /// </summary>
        internal static int GetTwoHandedLimit(bool canCarryTwoTwoHandedItems)
        {
            return canCarryTwoTwoHandedItems ? 2 : 1;
        }

        internal static bool IsSelectable(int slot, int unlockedSlotCount, int occupancyMask)
        {
            if (slot < 0 || slot >= PhysicalSlotCount)
                return false;

            return slot < unlockedSlotCount || (occupancyMask & (1 << slot)) != 0;
        }

        internal static int FindFirstEmptySlot(
            bool utilitySlotAvailable,
            int currentSlot,
            int unlockedSlotCount,
            int occupancyMask)
        {
            if (utilitySlotAvailable)
                return UtilitySlotIndex;

            int limit = ClampUnlockedCount(unlockedSlotCount);
            if (currentSlot >= 0
                && currentSlot < limit
                && (occupancyMask & (1 << currentSlot)) == 0)
            {
                return currentSlot;
            }

            for (int slot = 0; slot < limit; slot++)
            {
                if ((occupancyMask & (1 << slot)) == 0)
                    return slot;
            }

            return -1;
        }

        internal static int FindNextSelectableSlot(
            int currentSlot,
            bool forward,
            int unlockedSlotCount,
            int occupancyMask)
        {
            int limit = ClampUnlockedCount(unlockedSlotCount);
            if (currentSlot == UtilitySlotIndex)
            {
                if (forward)
                    return 0;

                for (int slot = PhysicalSlotCount - 1; slot >= 0; slot--)
                {
                    if (IsSelectable(slot, limit, occupancyMask))
                        return slot;
                }

                return limit - 1;
            }

            int start = currentSlot >= 0 && currentSlot < PhysicalSlotCount
                ? currentSlot
                : (forward ? PhysicalSlotCount - 1 : 0);

            for (int offset = 1; offset <= PhysicalSlotCount; offset++)
            {
                int slot = forward
                    ? (start + offset) % PhysicalSlotCount
                    : (start - offset + PhysicalSlotCount * 2) % PhysicalSlotCount;
                if (IsSelectable(slot, limit, occupancyMask))
                    return slot;
            }

            return limit - 1;
        }

        internal static int GetHighestUnlockedSlot(int unlockedSlotCount)
        {
            return ClampUnlockedCount(unlockedSlotCount) - 1;
        }

        private static int ClampUnlockedCount(int unlockedSlotCount)
        {
            if (unlockedSlotCount < VanillaSlotCount)
                return VanillaSlotCount;
            if (unlockedSlotCount > PhysicalSlotCount)
                return PhysicalSlotCount;
            return unlockedSlotCount;
        }
    }
}

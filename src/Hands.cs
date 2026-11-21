using System.Reflection;
using HarmonyLib;

namespace Beastkeeper
{
    // what a character holds in each hand (the game keeps these private)
    internal static class Hands
    {
        private static readonly FieldInfo RightF = AccessTools.Field(typeof(Humanoid), "m_rightItem");
        private static readonly FieldInfo LeftF = AccessTools.Field(typeof(Humanoid), "m_leftItem");

        internal static ItemDrop.ItemData Right(Humanoid h) { return h != null && RightF != null ? RightF.GetValue(h) as ItemDrop.ItemData : null; }
        internal static ItemDrop.ItemData Left(Humanoid h) { return h != null && LeftF != null ? LeftF.GetValue(h) as ItemDrop.ItemData : null; }
    }
}

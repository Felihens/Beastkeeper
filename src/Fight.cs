using System.Collections.Generic;
using UnityEngine;

namespace Beastkeeper
{
    // Fighting from the saddle: on a boar, wolf, bear or asksvin, attack uses the animal's own lighter attack
    // (its bite, its tusks, its swipe) and secondary attack its strongest one. The attack goes where the animal
    // faces, which is where you look. Each animal carries its attacks as items, the same ones its AI uses.
    internal static class Fight
    {
        private static readonly HashSet<string> Fighters = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { "Boar", "Wolf", "Bjorn", "Asksvin", "Hatchling" };

        internal static bool Fights(Character c) { return c != null && Fighters.Contains(Mount.Kind(c.gameObject)); }

        internal static void Attack(Character c, bool strong)
        {
            if (!Fights(c)) return;
            Humanoid h = c as Humanoid;
            if (h == null || h.InAttack() || c.IsDead()) return;
            ZNetView nv = Leash.View(c);
            if (nv == null || !nv.IsValid()) return;
            if (!nv.IsOwner()) nv.ClaimOwnership();

            List<ItemDrop.ItemData> moves = new List<ItemDrop.ItemData>();
            foreach (ItemDrop.ItemData it in h.GetInventory().GetAllItems())
                if (it != null && it.IsWeapon() && it.m_shared.m_attack != null && !string.IsNullOrEmpty(it.m_shared.m_attack.m_attackAnimation))
                    moves.Add(it);
            if (moves.Count == 0) { Plugin.Debug("[fight] " + c.name + " has no attacks"); return; }
            moves.Sort((a, b) => a.m_shared.m_damages.GetTotalDamage().CompareTo(b.m_shared.m_damages.GetTotalDamage()));

            ItemDrop.ItemData pick = strong ? moves[moves.Count - 1] : moves[0];
            // only one attack: the strong one is its second swing, if it has one
            bool secondary = strong && moves.Count == 1 && pick.HaveSecondaryAttack();
            if (h.GetCurrentWeapon() != pick) h.EquipItem(pick, false);
            bool ok = h.StartAttack(null, secondary);
            Plugin.Debug("[fight] " + c.name + (strong ? " strong " : " ") + "attack " + pick.m_shared.m_name
                + (secondary ? " (second swing)" : "") + ": " + (ok ? "go" : "refused") + " (" + moves.Count + " attacks)");
        }
    }
}

using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // Auto pickup while riding: the game pulls items in within 2 m of you, measured from the saddle, too high above
    // the ground for most mounts. Riding, items near the animal's feet are picked up too (same rules as the game:
    // auto pickup on, room in the bag, not over the weight limit). E is left as it was (it gets you off).
    [HarmonyPatch]
    internal static class RidePickup
    {
        private static readonly FieldInfo PickupMask = AccessTools.Field(typeof(Player), "m_autoPickupMask");
        private static readonly FieldInfo PickupOn = AccessTools.Field(typeof(Player), "m_enableAutoPickup");
        private static readonly FieldInfo PickupRange = AccessTools.Field(typeof(Player), "m_autoPickupRange");
        private static readonly Collider[] Near = new Collider[64];
        private static int _said;

        private static Character Ridden(Player p)
        {
            if (!Plugin.Enabled.Value || p == null || p != Player.m_localPlayer || Mount.Riding == null) return null;
            return Mount.Riding.GetControlledComponent() as Character;
        }

        [HarmonyPatch(typeof(Player), "AutoPickup"), HarmonyPostfix]
        private static void PickNearFeet(Player __instance)
        {
            Character mount = Ridden(__instance);
            if (mount == null || PickupOn == null || !(bool)PickupOn.GetValue(null) || __instance.IsTeleporting() || PickupMask == null || PickupRange == null) return;
            float range = (float)PickupRange.GetValue(__instance);
            CapsuleCollider cap = mount.GetComponent<CapsuleCollider>();
            float r = range + (cap != null ? cap.radius * Mathf.Max(mount.transform.lossyScale.x, mount.transform.lossyScale.z) : 0.5f);
            Vector3 feet = mount.transform.position + Vector3.up * 0.5f;
            int n = Physics.OverlapSphereNonAlloc(feet, r, Near, (int)PickupMask.GetValue(__instance));
            Inventory inv = __instance.GetInventory();
            for (int i = 0; i < n; i++)
            {
                Rigidbody rb = Near[i].attachedRigidbody;
                ItemDrop drop = rb != null ? rb.GetComponent<ItemDrop>() : null;
                if (drop == null || !drop.m_autoPickup || drop.IsPiece()) continue;
                if (__instance.HaveUniqueKey(drop.m_itemData.m_shared.m_name)) continue;
                ZNetView nv = drop.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) continue;
                if (!drop.CanPickup(true)) { drop.RequestOwn(); continue; }
                if (drop.InTar()) continue;
                drop.Load();
                if (!inv.CanAddItem(drop.m_itemData, -1)) continue;
                if (drop.m_itemData.GetWeight(-1) + inv.GetTotalWeight() > __instance.GetMaxCarryWeight()) continue;
                if (_said < 20) { _said++; Plugin.Debug("[ride] picked up " + drop.m_itemData.m_shared.m_name + " from the saddle"); }
                __instance.Pickup(drop.gameObject, true, true);
            }
        }
    }
}

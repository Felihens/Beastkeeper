using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // The stowed lasso hangs at your right hip instead of over your shoulder. The game puts it on the back
    // like any two-handed weapon; here it is moved, every frame, to the hips bone and turned to lie flat
    // against the thigh. Everyone with the mod sees it there.
    [HarmonyPatch]
    internal static class Hip
    {
        [HarmonyPatch(typeof(VisEquipment), "AttachBackItem"), HarmonyPostfix]
        private static void Back(VisEquipment __instance, int hash, GameObject __result)
        {
            if (__result == null || !Lasso.Set.HasPrefab(hash)) return;
            if (__result.GetComponent<OnHip>() == null) __result.AddComponent<OnHip>().Owner = __instance.transform;
        }
    }

    internal class OnHip : MonoBehaviour
    {
        internal Transform Owner;
        private Transform _hips;

        private void LateUpdate()
        {
            if (Owner == null) return;
            if (_hips == null)
            {
                Animator an = Owner.GetComponentInChildren<Animator>();
                if (an != null && an.isHuman) _hips = an.GetBoneTransform(HumanBodyBones.Hips);
                if (_hips == null) _hips = Find(Owner, "Hips");
                if (_hips == null) return;
            }
            Transform body = Owner;
            transform.position = _hips.position + body.right * 0.2f + body.up * -0.05f - body.forward * 0.03f;
            // the coil is drawn flat (its axis is "up"): stand it on edge against the right thigh
            transform.rotation = body.rotation * Quaternion.Euler(0f, 0f, -90f);
        }

        private static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t)
            {
                Transform r = Find(c, name);
                if (r != null) return r;
            }
            return null;
        }
    }
}

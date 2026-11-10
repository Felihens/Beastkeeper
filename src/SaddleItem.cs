using UnityEngine;

namespace Beastkeeper
{
    // The saddle as an item: the same leather saddle the animals wear, so you can see it on the ground and in your hand.
    internal class SaddleItemLook : MonoBehaviour
    {
        public int Tier = 1;   // public: kept when the game copies the item
        private const float Half = 0.26f, Depth = 0.9f, Pinch = 0.2f;
        private static Mesh _mesh;

        private void Start()
        {
            if (transform.Find("model") != null) return;
            GameObject m = new GameObject("model");
            m.layer = gameObject.layer;
            m.transform.SetParent(transform, false);
            m.transform.localPosition = new Vector3(0f, Half * Depth, 0f);   // its sides hang below the top: lift it off the ground
            if (_mesh == null) _mesh = SaddleMesh.Build(Half, Depth, Pinch);
            m.AddComponent<MeshFilter>().sharedMesh = _mesh;
            m.AddComponent<MeshRenderer>().sharedMaterial = SaddleMesh.Material(Tier);
        }
    }
}

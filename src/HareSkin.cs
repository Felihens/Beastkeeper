using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace Beastkeeper
{
    // A farm-born hare's own colour, on its fur only. The fur mask (white = fur) was painted on the hare's model
    // and ships inside the mod. Each colour is a copy of the hare's texture with the fur recoloured: it keeps the
    // fur's light and dark and takes the baby's hue. Copies are shared by hares of about the same colour.
    [HarmonyPatch]
    internal static class HareSkin
    {
        // colours are rounded to these steps, so hares of about the same colour share a texture
        private const int Hues = 48, Sats = 8, Vals = 10;
        private const float ValMin = 0.25f, ValMax = 1.45f;
        private static readonly Dictionary<int, Texture2D> ByHue = new Dictionary<int, Texture2D>();
        private static Color32[] _base, _mask;
        private static int _w, _h;
        private static bool _tried;

        // the recoloured texture for this hue, or null (no mask or no texture: the caller tints the whole hare)
        internal static Texture2D For(float hue, float sat, float val, Texture source)
        {
            if (!Ready(source)) return null;
            int h = Mathf.RoundToInt(Mathf.Repeat(hue, 1f) * Hues) % Hues;
            int s = Mathf.RoundToInt(Mathf.Clamp01(sat) * (Sats - 1));
            int v = Mathf.RoundToInt(Mathf.InverseLerp(ValMin, ValMax, val) * (Vals - 1));
            int key = (h * Sats + s) * Vals + v;
            Texture2D t;
            if (ByHue.TryGetValue(key, out t) && t != null) return t;
            Color col = Color.HSVToRGB(h / (float)Hues, s / (float)(Sats - 1), 1f);
            float light = Mathf.Lerp(ValMin, ValMax, v / (float)(Vals - 1));
            Color32[] px = new Color32[_base.Length];
            for (int i = 0; i < px.Length; i++)
            {
                Color32 b = _base[i];
                float a = _mask[i].r / 255f;
                if (a <= 0f) { px[i] = b; continue; }
                // the same sum the mask page shows: keep how light the fur is, give it the colour
                float k = Mathf.Min(1f, (0.3f * b.r + 0.59f * b.g + 0.11f * b.b) / 255f * 1.6f) * light;
                px[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.Lerp(b.r, col.r * k * 255f, a), 0f, 255f),
                    (byte)Mathf.Clamp(Mathf.Lerp(b.g, col.g * k * 255f, a), 0f, 255f),
                    (byte)Mathf.Clamp(Mathf.Lerp(b.b, col.b * k * 255f, a), 0f, 255f), b.a);
            }
            t = new Texture2D(_w, _h, TextureFormat.RGBA32, true);
            t.name = "Beastkeeper_Hare_" + key;
            t.SetPixels32(px);
            t.Apply(true, true);   // no longer readable: it lives on the GPU only
            ByHue[key] = t;
            return t;
        }

        private static readonly Dictionary<int, Texture2D> GlowByHue = new Dictionary<int, Texture2D>();

        // a glowing hare's light: its fur only (black everywhere else), in its colour at full brightness
        // (white for a white, grey or black one), keeping the fur's strands
        internal static Texture2D GlowFor(Color glow, Texture source)
        {
            if (!Ready(source)) return null;
            int key = ((Mathf.RoundToInt(glow.r * 15f) * 16) + Mathf.RoundToInt(glow.g * 15f)) * 16 + Mathf.RoundToInt(glow.b * 15f);
            Texture2D t;
            if (GlowByHue.TryGetValue(key, out t) && t != null) return t;
            Color32[] px = new Color32[_base.Length];
            for (int i = 0; i < px.Length; i++)
            {
                Color32 b = _base[i];
                float a = _mask[i].r / 255f;
                float k = a * Mathf.Clamp01(0.35f + (0.3f * b.r + 0.59f * b.g + 0.11f * b.b) / 255f * 1.3f);
                px[i] = new Color32((byte)(glow.r * k * 255f), (byte)(glow.g * k * 255f), (byte)(glow.b * k * 255f), 255);
            }
            t = new Texture2D(_w, _h, TextureFormat.RGBA32, true);
            t.name = "Beastkeeper_HareGlow_" + key;
            t.SetPixels32(px);
            t.Apply(true, true);
            GlowByHue[key] = t;
            return t;
        }

        private static bool Ready(Texture source)
        {
            if (_base != null) return true;
            if (_tried || source == null) return false;
            _tried = true;
            Texture2D mask = LoadMask();
            if (mask == null) { Plugin.Log.LogWarning("The hare fur mask is missing from the mod: baby hares are tinted all over."); return false; }
            _w = mask.width; _h = mask.height;
            _mask = mask.GetPixels32();
            Object.Destroy(mask);
            // game textures can't be read: copy it on the GPU first
            RenderTexture rt = RenderTexture.GetTemporary(_w, _h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture prev = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            Texture2D copy = new Texture2D(_w, _h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, _w, _h), 0, 0);
            copy.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            _base = copy.GetPixels32();
            Object.Destroy(copy);
            Plugin.Debug("[breed] hare fur mask loaded (" + _w + "x" + _h + ")");
            return true;
        }

        private static Texture2D LoadMask()
        {
            using (Stream s = typeof(HareSkin).Assembly.GetManifestResourceStream("Beastkeeper.hare_mask.png"))
            {
                if (s == null) return null;
                byte[] data = new byte[s.Length];
                int read = 0;
                while (read < data.Length) { int n = s.Read(data, read, data.Length - read); if (n <= 0) break; read += n; }
                Texture2D t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                return ImageConversion.LoadImage(t, data) ? t : null;
            }
        }
    }
}

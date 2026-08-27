using System.IO;
using UnityEditor;
using UnityEngine;

namespace LiquidFX.EditorTools
{
    /// <summary>
    /// Generates every texture the liquid effects need, procedurally.
    /// Keeping them generated rather than imported means the package has no binary dependencies
    /// and the textures can be retuned by editing numbers instead of round-tripping to an image
    /// editor. They are small on purpose: the largest is 256 px.
    /// </summary>
    public static class LiquidFXTextureFactory
    {
        public const string TextureFolder = LiquidFXPaths.Generated + "/Textures";

        public static void CreateAll()
        {
            LiquidFXPaths.EnsureFolder(TextureFolder);

            CreateMicroNormal();
            CreateStreamFlow();
            CreatePuddleNoise();
            CreateSoftDroplet();
            CreateSplashSheet();
            CreateImpactRing();

            AssetDatabase.Refresh();
        }

        // ------------------------------------------------------------------ water micro normal

        static void CreateMicroNormal()
        {
            const int size = 128;
            const float step = 1f / size;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x * step;
                    float v = y * step;

                    // Central differences on a tileable fbm give a normal map without seams.
                    float left = TileableFbm(u - step, v, 5f, 4);
                    float right = TileableFbm(u + step, v, 5f, 4);
                    float down = TileableFbm(u, v - step, 5f, 4);
                    float up = TileableFbm(u, v + step, 5f, 4);

                    Vector3 normal = new Vector3((left - right) * 6f, (down - up) * 6f, 1f).normalized;
                    texture.SetPixel(x, y, new Color(
                        normal.x * 0.5f + 0.5f,
                        normal.y * 0.5f + 0.5f,
                        normal.z * 0.5f + 0.5f,
                        1f));
                }
            }

            Save(texture, TextureFolder + "/WaterMicroNormal.png", TextureWrapMode.Repeat, false, TextureImporterType.NormalMap);
        }

        // ------------------------------------------------------------------ stream detail

        static void CreateStreamFlow()
        {
            const int width = 64;
            const int height = 256;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float u = (x + 0.5f) / width;
                    float v = (y + 0.5f) / height;

                    // Streaks running along the fall direction, stretched 6:1 so they read as flow.
                    float streaks = TileableFbm(u * 3.5f, v * 0.6f, 6f, 4);
                    float sharpen = Mathf.SmoothStep(0.35f, 0.75f, streaks);

                    // A little cross-jet banding, like the surface waves on a real jet.
                    float banding = 0.5f + 0.5f * Mathf.Sin(v * Mathf.PI * 2f * 7f);
                    float value = Mathf.Lerp(0.62f, 1.15f, sharpen * 0.75f + banding * 0.25f);

                    value = Mathf.Clamp01(value);
                    texture.SetPixel(x, y, new Color(value, value, value, 1f));
                }
            }

            Save(texture, TextureFolder + "/StreamFlow.png", TextureWrapMode.Repeat, false);
        }

        // ------------------------------------------------------------------ puddle edge noise

        static void CreatePuddleNoise()
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    float v = (y + 0.5f) / size;
                    float value = TileableFbm(u, v, 3.5f, 5);
                    value = Mathf.SmoothStep(0.15f, 0.85f, value);
                    texture.SetPixel(x, y, new Color(value, value, value, 1f));
                }
            }

            Save(texture, TextureFolder + "/PuddleNoise.png", TextureWrapMode.Repeat, false);
        }

        // ------------------------------------------------------------------ particles

        static void CreateSoftDroplet()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    float distance = Mathf.Sqrt(u * u + v * v);

                    float alpha = 1f - Mathf.SmoothStep(0.38f, 1f, distance);
                    // A droplet is a lens: bright rim, brighter still where the light pins through.
                    float rim = Mathf.SmoothStep(0.55f, 0.9f, distance) * (1f - Mathf.SmoothStep(0.9f, 1f, distance));
                    float core = 1f - Mathf.SmoothStep(0f, 0.55f, distance);
                    float luminance = Mathf.Clamp01(0.55f + core * 0.3f + rim * 0.75f);

                    texture.SetPixel(x, y, new Color(luminance, luminance, luminance, Mathf.Clamp01(alpha)));
                }
            }

            Save(texture, TextureFolder + "/SoftDroplet.png", TextureWrapMode.Clamp, true);
        }

        static void CreateSplashSheet()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    float v = (y + 0.5f) / size;

                    // A splash sheet is a thin membrane with a heavy rim that breaks into drops.
                    float across = Mathf.Abs(v - 0.5f) * 2f;
                    float membrane = (1f - Mathf.SmoothStep(0.1f, 0.95f, across)) * 0.22f;
                    float rim = Mathf.SmoothStep(0.52f, 0.82f, across) * (1f - Mathf.SmoothStep(0.85f, 1f, across));
                    float along = Mathf.Pow(Mathf.Clamp01(Mathf.Sin(u * Mathf.PI)), 0.3f);
                    float breakup = Mathf.Lerp(0.68f, 1f, TileableFbm(u, v, 5f, 3));

                    float alpha = Mathf.Clamp01((membrane + rim * 1.15f) * along * breakup);
                    texture.SetPixel(x, y, new Color(0.85f, 0.95f, 1f, alpha));
                }
            }

            Save(texture, TextureFolder + "/SplashSheet.png", TextureWrapMode.Clamp, true);
        }

        static void CreateImpactRing()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    float distance = Mathf.Sqrt(u * u + v * v);
                    float angle = Mathf.Atan2(v, u);

                    // A slightly wobbly ring reads as water; a perfect one reads as a decal.
                    float wobble = 1f + Mathf.Sin(angle * 7f) * 0.035f + Mathf.Sin(angle * 13f + 1.7f) * 0.02f;
                    float ringDistance = distance / wobble;

                    float alpha = Mathf.SmoothStep(0.66f, 0.79f, ringDistance)
                        * (1f - Mathf.SmoothStep(0.79f, 0.94f, ringDistance));

                    texture.SetPixel(x, y, new Color(0.8f, 0.95f, 1f, Mathf.Clamp01(alpha)));
                }
            }

            Save(texture, TextureFolder + "/ImpactRing.png", TextureWrapMode.Clamp, true);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Value noise sampled on a torus, so the result tiles seamlessly in both directions.
        /// </summary>
        static float TileableFbm(float u, float v, float frequency, int octaves)
        {
            float amplitude = 1f;
            float total = 0f;
            float normalisation = 0f;

            for (int octave = 0; octave < octaves; octave++)
            {
                float f = frequency * Mathf.Pow(2f, octave);
                total += amplitude * TileableValueNoise(u, v, f);
                normalisation += amplitude;
                amplitude *= 0.5f;
            }

            return normalisation <= 0f ? 0f : Mathf.Clamp01(total / normalisation);
        }

        static float TileableValueNoise(float u, float v, float frequency)
        {
            // Wrapping Perlin by mirroring the lattice: sample four corners of the unit square and
            // blend, which keeps the left edge identical to the right edge.
            float x = u * frequency;
            float y = v * frequency;
            float wrap = frequency;

            float a = Mathf.PerlinNoise(x, y);
            float b = Mathf.PerlinNoise(x - wrap, y);
            float c = Mathf.PerlinNoise(x, y - wrap);
            float d = Mathf.PerlinNoise(x - wrap, y - wrap);

            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        static void Save(
            Texture2D texture,
            string path,
            TextureWrapMode wrapMode,
            bool alphaIsTransparency,
            TextureImporterType type = TextureImporterType.Default)
        {
            texture.Apply();
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
                return;

            importer.textureType = type;
            importer.wrapMode = wrapMode;
            importer.filterMode = FilterMode.Bilinear;
            importer.alphaIsTransparency = alphaIsTransparency;
            importer.mipmapEnabled = true;
            importer.sRGBTexture = type == TextureImporterType.Default && alphaIsTransparency;
            importer.maxTextureSize = 256;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
        }
    }
}

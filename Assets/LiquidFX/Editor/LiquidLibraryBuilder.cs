using UnityEditor;
using UnityEngine;

namespace LiquidFX.EditorTools
{
    /// <summary>
    /// Generates a starter set of LiquidCategory/LiquidDefinition assets, the same way
    /// LiquidFXBuilder generates materials and prefabs from code instead of hand-authoring. Safe
    /// to re-run: existing assets at the expected path are updated in place, never duplicated.
    /// </summary>
    static class LiquidLibraryBuilder
    {
        [MenuItem("Tools/LiquidFX/Build Liquid Library", priority = 90)]
        internal static void BuildLibrary()
        {
            LiquidFXPaths.EnsureFolder(LiquidFXPaths.Library);
            LiquidFXPaths.EnsureFolder(LiquidFXPaths.Library + "/Categories");
            LiquidFXPaths.EnsureFolder(LiquidFXPaths.Library + "/Liquids");

            // Real relative densities, spaced well apart so no future category can land within
            // the validator's 0.01 collision-warning band by accident.
            LiquidCategory alcoholic = Category("Cat_Alcoholic", "Alcoholic", 0.79f, new Color(0.9f, 0.8f, 0.3f));
            LiquidCategory oily = Category("Cat_Oily", "Oily", 0.88f, new Color(0.85f, 0.65f, 0.15f));
            LiquidCategory aqueous = Category("Cat_Aqueous", "Aqueous", 1.00f, new Color(0.3f, 0.7f, 1f));
            LiquidCategory syrupy = Category("Cat_Syrupy", "Syrupy", 1.35f, new Color(0.9f, 0.3f, 0.6f));
            LiquidCategory denseAcid = Category("Cat_DenseAcid", "Dense Acid", 1.84f, new Color(0.9f, 0.9f, 0.2f));
            LiquidCategory metallic = Category("Cat_Metallic", "Metallic", 13.5f, new Color(0.75f, 0.75f, 0.8f));

            // Raising alpha alone did not fix visibility (tested up to 0.85): a pale, low-saturation
            // colour blends into a bright sky/background almost regardless of alpha, while a fully
            // saturated one (tested with plain red) reads clearly even at moderate alpha. So these
            // are tuned for hue distance from white/grey first, alpha second - every liquid keeps a
            // real, identifiable colour instead of "clear with a tint" that vanishes on some meshes.
            Liquid("Liq_Water", "Water", aqueous, new Color(0.1f, 0.45f, 0.95f, 0.55f));
            Liquid("Liq_SaltWater", "Salt Water", aqueous, new Color(0.05f, 0.65f, 0.7f, 0.58f));
            Liquid("Liq_VegetableOil", "Vegetable Oil", oily, new Color(0.95f, 0.75f, 0.1f, 0.62f));
            // Ethanol is the topmost liquid in most of the showcase flasks (lowest density of the
            // library), so it is the one most exposed to washing out - amber spirit tint instead of
            // near-clear.
            Liquid("Liq_Ethanol", "Ethanol", alcoholic, new Color(0.9f, 0.6f, 0.15f, 0.55f));
            Liquid("Liq_Syrup", "Syrup", syrupy, new Color(0.75f, 0.15f, 0.35f, 0.75f));
            Liquid("Liq_SulfuricAcid", "Sulfuric Acid", denseAcid, new Color(0.65f, 0.85f, 0.05f, 0.7f));
            Liquid("Liq_Mercury", "Mercury", metallic, new Color(0.75f, 0.76f, 0.8f, 0.92f));

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("LiquidFX: liquid library built (6 categories, 7 liquids).");
        }

        static LiquidCategory Category(string fileName, string displayName, float density, Color tint)
        {
            string path = LiquidFXPaths.Library + "/Categories/" + fileName + ".asset";
            var asset = AssetDatabase.LoadAssetAtPath<LiquidCategory>(path);
            bool isNew = asset == null;
            if (isNew)
            {
                asset = ScriptableObject.CreateInstance<LiquidCategory>();
                AssetDatabase.CreateAsset(asset, path);
            }

            var so = new SerializedObject(asset);
            so.FindProperty("displayName").stringValue = displayName;
            so.FindProperty("stackDensity").floatValue = density;
            so.FindProperty("editorTint").colorValue = tint;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return asset;
        }

        static LiquidDefinition Liquid(string fileName, string displayName, LiquidCategory category, Color color)
        {
            string path = LiquidFXPaths.Library + "/Liquids/" + fileName + ".asset";
            var asset = AssetDatabase.LoadAssetAtPath<LiquidDefinition>(path);
            bool isNew = asset == null;
            if (isNew)
            {
                asset = ScriptableObject.CreateInstance<LiquidDefinition>();
                AssetDatabase.CreateAsset(asset, path);
            }

            var so = new SerializedObject(asset);
            so.FindProperty("displayName").stringValue = displayName;
            so.FindProperty("category").objectReferenceValue = category;
            so.FindProperty("color").colorValue = color;
            so.FindProperty("murkColor").colorValue = Color.black;
            // Kept low: LiquidVolumePro's turbulence pattern is a fixed-frequency noise, so on a
            // small showcase container (a few centimetres across) even a moderate murkiness reads
            // as coarse sandy grain rather than a soft cloudy liquid. A clear, mixable liquid
            // should look glassy first and murky only as a deliberate per-liquid exaggeration.
            so.FindProperty("murkiness").floatValue = 0.12f;
            so.FindProperty("scale").floatValue = 0.12f;
            so.FindProperty("viscosity").floatValue = 1f;
            so.FindProperty("bubblesOpacity").floatValue = 0.5f;
            so.FindProperty("adjustmentSpeed").floatValue = 1f;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return asset;
        }
    }
}

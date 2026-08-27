using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LabSpill.EditorTools
{
    /// <summary>
    /// Turns the JSON liquid configs this project was authored with into
    /// <see cref="SpillLiquidCategory"/> / <see cref="SpillLiquidDefinition"/> assets.
    ///
    /// Deliberately reads the JSON through its own DTO instead of through LiquidConfig: that class
    /// belongs to the flask system being replaced and will be deleted, and a converter that stops
    /// compiling the moment its input format is retired is a converter nobody can re-run.
    ///
    /// Safe to re-run - assets at the expected path are updated in place, never duplicated.
    /// </summary>
    static class SpillLiquidLibraryBuilder
    {
        const string LibraryRoot = "Assets/LabSpillClean/Liquids";
        const string ConfigRoot = "Assets/LabSpillClean/Configs";

        /// <summary>
        /// The JSON fields this conversion actually reads. The configs carry many more (wave
        /// shape, bubble sizes, foam), all of which describe the old shader and have no home in a
        /// LiquidVolumePro layer.
        /// </summary>
        [System.Serializable]
        sealed class ConfigDto
        {
            public string liquidName;
            public string category;
            public float densityKgPerLiter = 1f;
            public float viscosity = 1f;
            public float boilingPointC = 100f;
            public Color bodyColor = Color.white;
            public Color deepColor = Color.black;
            public Color vaporColor = new Color(0.94f, 0.98f, 1f, 0.18f);
            public float absorptionDensity = 2.6f;
            public float turbidity = 0.18f;
            public float steamRateAtMaximum = 5f;
            public float steamStartIntensity = 0.65f;
        }

        [MenuItem("Tools/Lab Spill/Converter JSONs em assets de liquido", priority = 90)]
        static void Convert()
        {
            EnsureFolder(LibraryRoot);
            EnsureFolder(LibraryRoot + "/Categories");
            EnsureFolder(LibraryRoot + "/Liquids");

            string[] files = Directory.GetFiles(ConfigRoot, "*.json");
            if (files.Length == 0)
            {
                Debug.LogWarning($"Lab Spill: nenhum JSON encontrado em {ConfigRoot}.");
                return;
            }

            // The JSON "category" string is the mixing family and is reused verbatim, so water and
            // alcohol (both "polar") keep mixing exactly as they do today. Density stays on each
            // liquid, which is what lets alcohol still float on oil - see SpillLiquidCategory for
            // why that separation had to be rebuilt rather than inherited from LiquidFX.
            var categories = new Dictionary<string, SpillLiquidCategory>();
            var converted = new List<string>();

            foreach (string file in files)
            {
                ConfigDto dto = JsonUtility.FromJson<ConfigDto>(File.ReadAllText(file));
                if (dto == null || string.IsNullOrEmpty(dto.liquidName))
                {
                    Debug.LogWarning($"Lab Spill: {file} nao pode ser lido como config de liquido.");
                    continue;
                }

                string categoryKey = string.IsNullOrEmpty(dto.category) ? "sem-categoria" : dto.category;
                if (!categories.TryGetValue(categoryKey, out SpillLiquidCategory category))
                {
                    category = Category(categoryKey, dto.bodyColor);
                    categories.Add(categoryKey, category);
                }

                Liquid(Path.GetFileNameWithoutExtension(file), dto, category);
                converted.Add(dto.liquidName);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"Lab Spill: {converted.Count} liquido(s) convertido(s) em " +
                $"{categories.Count} categoria(s): {string.Join(", ", converted)}.");
        }

        static SpillLiquidCategory Category(string key, Color tint)
        {
            string fileName = "Cat_" + Capitalise(key);
            string path = LibraryRoot + "/Categories/" + fileName + ".asset";
            var asset = AssetDatabase.LoadAssetAtPath<SpillLiquidCategory>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<SpillLiquidCategory>();
                AssetDatabase.CreateAsset(asset, path);
            }

            var so = new SerializedObject(asset);
            so.FindProperty("displayName").stringValue = Capitalise(key);
            so.FindProperty("editorTint").colorValue = tint;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return asset;
        }

        static void Liquid(string configFileName, ConfigDto dto, SpillLiquidCategory category)
        {
            // WaterLiquidConfig -> Liq_Water
            string stem = configFileName.Replace("LiquidConfig", string.Empty);
            string path = LibraryRoot + "/Liquids/Liq_" + stem + ".asset";
            var asset = AssetDatabase.LoadAssetAtPath<SpillLiquidDefinition>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<SpillLiquidDefinition>();
                AssetDatabase.CreateAsset(asset, path);
            }

            var so = new SerializedObject(asset);
            so.FindProperty("displayName").stringValue = dto.liquidName;
            so.FindProperty("category").objectReferenceValue = category;
            so.FindProperty("densityKgPerLiter").floatValue = dto.densityKgPerLiter;

            // LiquidVolumePro reads alpha as absorption strength, which is what absorptionDensity
            // meant in the old shader - not bodyColor's alpha, which was surface opacity and means
            // nothing here. Normalised over 0..5, the range SpillFluidWorld actually clamped the
            // old _Absorption to, rather than the field's nominal 0..12 ceiling that no config in
            // this project came close to: dividing by 12 puts every liquid under alpha 0.27 and
            // they all render nearly clear.
            Color color = dto.bodyColor;
            color.a = Mathf.Clamp01(dto.absorptionDensity / 5f);
            so.FindProperty("color").colorValue = color;

            so.FindProperty("murkColor").colorValue = dto.deepColor;
            so.FindProperty("murkiness").floatValue = Mathf.Clamp01(dto.turbidity);
            // Not derived from the JSON. The old configs' closest field, waveDetail, describes
            // surface wave detail and has nothing to do with LiquidVolumePro's scale, which is the
            // size of the volumetric noise pattern; mapping one onto the other landed every liquid
            // at ~0.41, near the 0.48 ceiling, which on a container a few centimetres across reads
            // as coarse grain instead of a liquid. Starts low and uniform, to be tuned per liquid
            // by eye.
            so.FindProperty("scale").floatValue = 0.12f;

            // LiquidVolumePro's viscosity slider is a 0..1 look control, not centipoise. Same log
            // remap the old shader used for _Viscosity01, so oil at 68 mPa.s stays near the top of
            // the range without water at 1 collapsing to zero.
            so.FindProperty("viscosity").floatValue =
                Mathf.Clamp01(Mathf.Log10(Mathf.Max(1f, dto.viscosity)) / 3.3f);

            so.FindProperty("bubblesOpacity").floatValue = 0.5f;
            so.FindProperty("adjustmentSpeed").floatValue = 1f;

            so.FindProperty("boilingPointC").floatValue = dto.boilingPointC;
            so.FindProperty("vaporColor").colorValue = dto.vaporColor;
            so.FindProperty("steamRateAtMaximum").floatValue = dto.steamRateAtMaximum;
            so.FindProperty("steamStartIntensity").floatValue = dto.steamStartIntensity;

            Color stream = dto.bodyColor;
            stream.a = 1f;
            so.FindProperty("streamColor").colorValue = stream;
            so.FindProperty("physicalViscosity").floatValue = Mathf.Max(0.2f, dto.viscosity);

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        static string Capitalise(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}

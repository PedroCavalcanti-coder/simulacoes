using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LiquidFX.EditorTools
{
    /// <summary>
    /// Sanity-checks every LiquidCategory/LiquidDefinition asset in the project against the rules
    /// the runtime code silently depends on. LiquidVolumePro merges two layers only when their
    /// density matches exactly (see LiquidVolume.UpdateLayersNow), so a category density collision
    /// is not cosmetic - it is two unrelated liquids mixing on contact with no warning at runtime.
    /// This is the place that catches it before someone hits play.
    /// </summary>
    static class LiquidLibraryValidator
    {
        [MenuItem("Tools/LiquidFX/Validate Liquid Library", priority = 100)]
        static void Validate()
        {
            var categories = LoadAll<LiquidCategory>();
            var definitions = LoadAll<LiquidDefinition>();

            int errors = 0;
            int warnings = 0;

            // 1. Two categories sharing a stacking density: LiquidVolumePro would treat their
            //    liquids as miscible with each other, which defeats the entire point of having
            //    separate categories.
            for (int i = 0; i < categories.Count; i++)
            {
                for (int j = i + 1; j < categories.Count; j++)
                {
                    float a = categories[i].asset.StackDensity;
                    float b = categories[j].asset.StackDensity;

                    if (Mathf.Approximately(a, b))
                    {
                        errors++;
                        Debug.LogError(
                            $"LiquidFX: categories '{categories[i].asset.DisplayName}' " +
                            $"({categories[i].path}) and '{categories[j].asset.DisplayName}' " +
                            $"({categories[j].path}) share stackDensity {a:0.###} - " +
                            "LiquidVolumePro will merge their liquids on contact. Give each a distinct value.");
                    }
                    else if (Mathf.Abs(a - b) < 0.01f)
                    {
                        warnings++;
                        Debug.LogWarning(
                            $"LiquidFX: categories '{categories[i].asset.DisplayName}' and " +
                            $"'{categories[j].asset.DisplayName}' have stackDensity within 0.01 of " +
                            $"each other ({a:0.###} vs {b:0.###}). They will not mix (LiquidVolumePro " +
                            "compares exactly), but the stacking order is fragile to future edits - space them further apart.");
                    }
                }
            }

            // 2. A liquid with no category has no stacking density source (falls back to 1,
            //    silently colliding with anything else that also has none).
            foreach (var (definition, path) in definitions)
            {
                if (definition.Category == null)
                {
                    errors++;
                    Debug.LogError($"LiquidFX: liquid '{definition.DisplayName}' ({path}) has no category assigned.");
                }

                if (definition.Color.a <= 0.001f)
                {
                    warnings++;
                    Debug.LogWarning(
                        $"LiquidFX: liquid '{definition.DisplayName}' ({path}) has alpha 0 - it will be " +
                        "invisible. LiquidVolumePro alpha is absorption strength, not opacity, so this " +
                        "is almost always a mistake rather than an intentionally clear liquid.");
                }
            }

            if (errors == 0 && warnings == 0)
                Debug.Log($"LiquidFX: liquid library OK ({categories.Count} categories, {definitions.Count} liquids).");
            else
                Debug.Log($"LiquidFX: liquid library validation found {errors} error(s), {warnings} warning(s).");
        }

        static List<(T asset, string path)> LoadAll<T>() where T : Object
        {
            var results = new List<(T, string)>();
            string[] guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null)
                    results.Add((asset, path));
            }
            return results;
        }
    }
}

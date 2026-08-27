// Copia de Assets/LiquidFX (pasta de exemplo, somente-leitura). Tipos e namespace
// renomeados para conviver com o original no mesmo projeto. Ver PLANO-REFORMA.md, tarefa 2.0.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LabSpill.EditorTools
{
    /// <summary>
    /// Sanity-checks every SpillLiquidCategory/SpillLiquidDefinition asset against the rules the
    /// runtime silently depends on.
    ///
    /// The LiquidFX original checked for two categories sharing a stacking density, because there
    /// density was also the mixing key. Here it is not: categories carry no density, mixing is
    /// decided by category identity, and every layer goes to LiquidVolumePro with
    /// <c>miscible = false</c>. So the checks below are about the two things that can still go
    /// wrong - a liquid with no family, and two liquids that will stack in an undefined order.
    /// </summary>
    static class SpillLiquidLibraryValidator
    {
        [MenuItem("Tools/Lab Spill/Validar biblioteca de liquidos", priority = 100)]
        static void Validate()
        {
            var definitions = LoadAll<SpillLiquidDefinition>();
            var categories = LoadAll<SpillLiquidCategory>();

            int errors = 0;
            int warnings = 0;

            foreach (var (definition, path) in definitions)
            {
                // No category means no mixing family. FindSlotWithCategory treats null as its own
                // family, so such a liquid merges only with other uncatalogued ones - almost never
                // what someone authoring a library intended.
                if (definition.Category == null)
                {
                    errors++;
                    Debug.LogError($"Lab Spill: liquido '{definition.DisplayName}' ({path}) esta sem categoria.");
                }

                if (definition.Color.a <= 0.001f)
                {
                    warnings++;
                    Debug.LogWarning(
                        $"Lab Spill: liquido '{definition.DisplayName}' ({path}) tem alpha 0 e ficara " +
                        "invisivel. No LiquidVolumePro o alpha e forca de absorcao, nao opacidade, " +
                        "entao isso quase sempre e engano.");
                }
            }

            // Two liquids of different families at the same density have no defined stacking
            // order: LiquidVolumePro breaks the tie by array position, which is whichever was
            // poured first. Same family is fine - they become one layer anyway.
            for (int i = 0; i < definitions.Count; i++)
            {
                for (int j = i + 1; j < definitions.Count; j++)
                {
                    SpillLiquidDefinition a = definitions[i].asset;
                    SpillLiquidDefinition b = definitions[j].asset;
                    if (a.Category == b.Category)
                        continue;

                    if (Mathf.Approximately(a.Density, b.Density))
                    {
                        warnings++;
                        Debug.LogWarning(
                            $"Lab Spill: '{a.DisplayName}' e '{b.DisplayName}' sao de categorias " +
                            $"diferentes mas tem a mesma densidade ({a.Density:0.###} kg/L). Qual " +
                            "fica embaixo passa a depender da ordem em que foram despejados.");
                    }
                }
            }

            if (errors == 0 && warnings == 0)
                Debug.Log($"Lab Spill: biblioteca OK ({categories.Count} categorias, {definitions.Count} liquidos).");
            else
                Debug.Log($"Lab Spill: validacao encontrou {errors} erro(s) e {warnings} aviso(s).");
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

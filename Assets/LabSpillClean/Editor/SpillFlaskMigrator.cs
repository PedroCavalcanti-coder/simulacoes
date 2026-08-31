using System.Collections.Generic;
using LabLiquidVR;
using LiquidVolumeFX;
using UnityEditor;
using UnityEngine;

namespace LabSpill.EditorTools
{
    /// <summary>
    /// Converte um frasco do sistema antigo para LiquidVolumePro + SpillFlaskVolume.
    ///
    /// Existe porque a migracao e trabalho de cena, e faze-la a mao em sete frascos e
    /// onde erros silenciosos entram: uma capacidade digitada errado, uma camada com o
    /// liquido trocado, um emissor apontando para o componente antigo. A conversao le o
    /// que o frasco antigo ja declara e escreve o equivalente.
    ///
    /// Nao apaga nada. O componente antigo e apenas desativado, entao dá para comparar
    /// os dois lado a lado e voltar atras enquanto a migracao nao estiver validada.
    /// </summary>
    static class SpillFlaskMigrator
    {
        const string LiquidsRoot = "Assets/LabSpillClean/Liquids/Liquids";

        [MenuItem("Tools/Lab Spill/Migrar frascos selecionados para LVP", priority = 120)]
        static void MigrateSelection()
        {
            var containers = new List<SpillLiquidContainer>();
            foreach (GameObject go in Selection.gameObjects)
                containers.AddRange(go.GetComponentsInChildren<SpillLiquidContainer>(true));

            if (containers.Count == 0)
            {
                EditorUtility.DisplayDialog("Lab Spill",
                    "Selecione um ou mais frascos que ainda tenham SpillLiquidContainer.", "OK");
                return;
            }

            int migrated = 0;
            foreach (SpillLiquidContainer container in containers)
                if (Migrate(container)) migrated++;

            Debug.Log($"Lab Spill: {migrated} de {containers.Count} frasco(s) migrado(s). " +
                "O componente antigo ficou desativado no objeto, nao apagado.");
        }

        [MenuItem("Tools/Lab Spill/Migrar frascos selecionados para LVP", true)]
        static bool MigrateSelectionValidate() => Selection.gameObjects.Length > 0;

        static bool Migrate(SpillLiquidContainer container)
        {
            GameObject target = container.gameObject;

            if (target.GetComponent<SpillFlaskVolume>() != null)
            {
                Debug.LogWarning($"Lab Spill: '{target.name}' ja tem SpillFlaskVolume; pulado.", target);
                return false;
            }

            var filter = target.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
            {
                Debug.LogError($"Lab Spill: '{target.name}' nao tem malha; o LiquidVolume " +
                    "precisa de uma para delimitar o volume.", target);
                return false;
            }

            Undo.RegisterFullObjectHierarchyUndo(target, "Migrar frasco para LVP");

            var volume = Undo.AddComponent<LiquidVolume>(target);
            // MultipleNoFlask: camadas ligadas, e o vidro continua sendo desenhado pela
            // malha do objeto pai. Trocar para Multiple faria o LVP desenhar um frasco
            // por cima do que ja existe na cena.
            volume.detail = DETAIL.MultipleNoFlask;
            volume.topology = TOPOLOGY.Irregular;

            var flask = Undo.AddComponent<SpillFlaskVolume>(target);
            var so = new SerializedObject(flask);
            so.FindProperty("capacityML").floatValue = container.capacityML;

            SerializedProperty contents = so.FindProperty("initialContents");
            contents.ClearArray();

            foreach ((SpillLiquidDefinition liquid, float millilitres) in ReadContents(container))
            {
                int i = contents.arraySize;
                contents.arraySize++;
                SerializedProperty element = contents.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("liquid").objectReferenceValue = liquid;
                element.FindPropertyRelative("millilitres").floatValue = millilitres;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            flask.BakeInitialContents();

            // O emissor mora no pai e precisa passar a apontar para o frasco novo, senao
            // continuaria derramando pelo caminho antigo de um container desativado.
            var emitter = target.GetComponentInParent<SpillPourEmitter>();
            if (emitter != null)
            {
                Undo.RecordObject(emitter, "Apontar emissor para o frasco novo");
                emitter.flask = flask;
                emitter.source = null;
                EditorUtility.SetDirty(emitter);
            }
            else
            {
                Debug.LogWarning($"Lab Spill: '{target.name}' migrado, mas nao achei " +
                    "SpillPourEmitter no pai: este frasco nao vai derramar.", target);
            }

            container.enabled = false;
            EditorUtility.SetDirty(container);
            EditorUtility.SetDirty(target);
            return true;
        }

        /// <summary>
        /// Le o conteudo declarado no frasco antigo. A composicao inicial tem prioridade
        /// sobre o par volume+config, que e o caso de frasco de liquido unico.
        /// </summary>
        static IEnumerable<(SpillLiquidDefinition, float)> ReadContents(SpillLiquidContainer container)
        {
            if (container.initialComposition != null && container.initialComposition.Count > 0)
            {
                foreach (InitialLiquidPortion portion in container.initialComposition)
                {
                    if (portion.milliliters <= 0f) continue;
                    SpillLiquidDefinition liquid = FindDefinition(portion.configFile);
                    if (liquid != null) yield return (liquid, portion.milliliters);
                }
                yield break;
            }

            if (container.currentVolumeML <= 0f) yield break;
            SpillLiquidDefinition single = FindDefinition(container.liquidConfigFile);
            if (single != null) yield return (single, container.currentVolumeML);
        }

        /// <summary>
        /// Acha o asset de liquido que veio do JSON. O conversor nomeia pelo arquivo:
        /// WaterLiquidConfig.json vira Liq_Water.asset.
        /// </summary>
        static SpillLiquidDefinition FindDefinition(LiquidConfigAsset config)
        {
            string stem = config != null
                ? config.name.Replace("LiquidConfig", string.Empty)
                : "Default";
            string path = $"{LiquidsRoot}/Liq_{stem}.asset";
            var asset = AssetDatabase.LoadAssetAtPath<SpillLiquidDefinition>(path);

            if (asset == null)
                Debug.LogError($"Lab Spill: nao achei '{path}'. Rode " +
                    "Tools > Lab Spill > Converter JSONs em assets de liquido antes de migrar.");

            return asset;
        }
    }
}

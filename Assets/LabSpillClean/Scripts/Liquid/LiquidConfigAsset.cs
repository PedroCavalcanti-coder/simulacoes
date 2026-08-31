using UnityEngine;

namespace LabLiquidVR
{
    /// <summary>
    /// Substitui o JSON como fonte do <see cref="LiquidConfig"/> de um frasco do sistema
    /// antigo (<see cref="SpillLiquidContainer"/>). O JSON exigia reabrir um editor de
    /// texto e um erro de sintaxe caia em silencio nos valores padrao (Load engolia a
    /// excecao); um ScriptableObject e editado pelo Inspector, valida os ranges pelos
    /// atributos dos campos e aparece no diff do Unity como campos, nao como texto solto.
    ///
    /// <see cref="LiquidConfig"/> continua sendo uma classe simples, nao um
    /// ScriptableObject: ele tambem serve de valor de trabalho em tempo real -
    /// LiquidComposition.Mix/Copy criam uma instancia nova a cada vez que um liquido se
    /// mistura, o que pode acontecer varias vezes por segundo enquanto se derrama. Se
    /// LiquidConfig virasse ScriptableObject, cada mistura alocaria um objeto nativo do
    /// Unity que precisaria de Destroy() explicito para nao vazar - exatamente o tipo de
    /// custo por frame que este projeto esta tentando eliminar, nao adicionar. Este asset
    /// e' so o container estatico: o que o artista edita, guardado uma vez por frasco.
    /// </summary>
    [CreateAssetMenu(menuName = "Lab Spill/Legacy Liquid Config (substitui o JSON)",
        fileName = "NovoLiquidConfig")]
    public sealed class LiquidConfigAsset : ScriptableObject
    {
        public LiquidConfig data = new LiquidConfig();
    }
}

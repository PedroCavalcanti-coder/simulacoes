using UnityEngine;

namespace LabSpill
{
    /// <summary>
    /// Marcador: o fluido atravessa os colisores deste objeto e dos filhos.
    ///
    /// Existe por causa da vidraria. Todo frasco da cena tem um BoxCollider, e desde
    /// que a particula passou a colidir com tudo essa caixa virou tampa: a gota
    /// mirada na boca bateria no topo do frasco e nunca chegaria ao porto que a
    /// converte em volume. A gota que erra o frasco passa ao lado e cai na bancada,
    /// que continua sendo colisor de verdade.
    ///
    /// Precisa morar num arquivo com o proprio nome: o Unity so cria MonoScript para
    /// a classe homonima ao arquivo, e um MonoBehaviour declarado junto de outra
    /// classe nao pode ser anexado a nenhum GameObject.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SpillColliderExclude : MonoBehaviour { }
}

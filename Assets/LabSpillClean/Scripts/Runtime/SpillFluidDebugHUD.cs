using UnityEngine;

namespace LabSpill
{
    /// <summary>
    /// Painel de diagnostico do fluido, no canto da tela durante o Play.
    ///
    /// Existe porque a reforma do nucleo de particulas e, de proposito, invisivel: pool
    /// de capacidade fixa, nascimento na GPU, morte assincrona e captura por substep
    /// nao mudam um pixel do que aparece na tela. Sem numeros, a unica avaliacao
    /// possivel e "parece igual", que nao distingue funcionando de quebrado.
    ///
    /// Ponha no mesmo objeto do <see cref="SpillFluidWorld"/> e rode.
    /// </summary>
    [RequireComponent(typeof(SpillFluidWorld))]
    public sealed class SpillFluidDebugHUD : MonoBehaviour
    {
        [Tooltip("Desliga o painel sem remover o componente.")]
        public bool show = true;

        [Tooltip("Canto da tela em pixels.")]
        public Vector2 origin = new Vector2(12f, 12f);

        SpillFluidWorld m_world;
        GUIStyle m_style;

        int m_lastSpawned;
        int m_lastAged;
        int m_lastCaptured;
        float m_window;
        float m_spawnRate;
        float m_ageRate;
        float m_captureRate;

        void Awake() => m_world = GetComponent<SpillFluidWorld>();

        void Update()
        {
            m_window += Time.deltaTime;
            if (m_window < 0.5f) return;

            m_spawnRate = (m_world.SpawnedTotal - m_lastSpawned) / m_window;
            m_ageRate = (m_world.DiedByAge - m_lastAged) / m_window;
            m_captureRate = (m_world.DiedByCapture - m_lastCaptured) / m_window;

            m_lastSpawned = m_world.SpawnedTotal;
            m_lastAged = m_world.DiedByAge;
            m_lastCaptured = m_world.DiedByCapture;
            m_window = 0f;
        }

        void OnGUI()
        {
            if (!show || m_world == null) return;

            m_style ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                richText = true,
                normal = { textColor = Color.white }
            };

            int alive = m_world.ParticleCount;
            int capacity = Mathf.Max(1, m_world.Capacity);

            string text =
                $"<b>Lab Spill</b>\n" +
                $"particulas   {alive} / {capacity}  ({100f * alive / capacity:0.#}%)\n" +
                $"nascendo     {m_spawnRate:0.#}/s   total {m_world.SpawnedTotal}\n" +
                $"morrendo     {m_ageRate:0.#}/s por idade   {m_captureRate:0.#}/s capturadas\n" +
                $"colisores    {m_world.ColliderCount}\n" +
                $"bocas        {m_world.PortCount}\n" +
                $"coesao       {m_world.Settings.cohesion:0.##} m/s2   " +
                $"iter {m_world.Settings.solverIterations}/{m_world.Settings.constraintIterations}\n" +
                $"dominio      {m_world.SimulationBounds.size.x:0.#} x " +
                $"{m_world.SimulationBounds.size.y:0.#} x {m_world.SimulationBounds.size.z:0.#} m";

            var size = new Vector2(340f, 132f);
            var box = new Rect(origin.x, origin.y, size.x, size.y);
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(box.x + 8f, box.y + 6f, box.width - 16f, box.height - 12f), text, m_style);
        }
    }
}

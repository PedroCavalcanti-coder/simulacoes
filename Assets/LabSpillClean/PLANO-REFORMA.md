# Plano de Reforma — LabSpillClean

Documento de execução. Escrito para ser seguido direto, inclusive por outro modelo de IA
nas tarefas marcadas **[S]**.

**Legenda de complexidade**
- **[S] Simples** — mecânico, escopo fechado, pode ir para modelo menor. Instruções literais.
- **[M] Médio** — precisa entender duas partes do sistema ao mesmo tempo.
- **[C] Complexo** — projeto novo de código/GPU. Não delegar.

**Decisões já tomadas (não reabrir)**
1. O líquido dentro do frasco passa a ser **LiquidVolumePro (detail = Multiple) + FlaskVolume**. Todo o caminho de render/volume do `SpillLiquidContainer` morre.
2. Os líquidos viram **ScriptableObject** (`LiquidDefinition` + `LiquidCategory`), com conversor one-shot a partir dos 4 JSONs atuais.
3. A física de partículas é **reescrita com pool de capacidade fixa na GPU**.

**Restrição de pastas (2026-08-27)**
`Assets/LiquidFX` e `Assets/LiquidVolumePro` são material de exemplo e **não podem ser
alterados**. Todo código novo ou modificado mora em `Assets/LabSpillClean`. O que precisar
ser mexido é **copiado** para lá com nome próprio.

Leitura em vigor (corrigir se estiver errada):
- `LiquidVolumePro` é **usado como está**, sem cópia — colocar um `LiquidVolume` no frasco não
  altera a pasta. Só vira cópia se aparecer necessidade de patch no asset.
- `LiquidFX` **será copiado**, porque a Fase 2.2 precisa estender `LiquidDefinition`. Cinco
  arquivos vão para `LabSpillClean/Scripts/Liquid/` com namespace `LabSpill`:
  `LiquidDefinition`, `LiquidCategory`, `FlaskVolume`, `ILiquidContainer`,
  `LiquidContainerRegistry` — renomeados com prefixo `Spill` (ex.: `SpillFlaskVolume`) para não
  colidir com os originais, que continuam compilando no projeto.

---

## 0. Diagnóstico — por que reescrever

### 0.1 O gargalo real da física

`FluidBody.Append` ([FluidBody.cs:195](Scripts/Core/FluidBody.cs:195)) faz, **a cada flush de emissão**:
- 10 × `ComputeBuffer.GetData` (stall GPU→CPU síncrono);
- 10 × `Release()` + `new ComputeBuffer()` + `SetData` (realocação total);
- e o chamador ainda executa `CreateSolver()` ([SpillFluidWorld.cs:334](Scripts/Runtime/SpillFluidWorld.cs:334)), que recria `GridHash` + `BitonicSort`.

`emissionFlushInterval = 0.025 s` ⇒ isso acontece **40 vezes por segundo** enquanto se derrama.

`CompactDead` ([FluidBody.cs:270](Scripts/Core/FluidBody.cs:270)) repete o padrão (7 readbacks + realloc) **1× por segundo**, sempre.

`UpdateLifecycle` ([SpillFluidWorld.cs:366](Scripts/Runtime/SpillFluidWorld.cs:366)) faz `Positions.GetData` + `States.GetData` **a cada 0,1 s**, e `MarkDead` faz um `SetData` de 1 elemento **por partícula morta**.

Nenhum ajuste de parâmetro conserta isso. É arquitetura.

### 0.2 O que o usuário mandou remover

- Líquido que cai no chão e some (`SpillSurfaceKind.Ground` → `SetKillColliders` → kernel `SolveKillColliders`).
- Colisão só com certas layers (`SpillSurface` Bench/Ground é a única fonte de colisor).
- `IsOnBench` + `benchLifetimeMin/Max` (morte condicionada à bancada).

**Regra nova:** partícula colide com **tudo** e morre por **tempo de vida**, em qualquer lugar.

### 0.3 O que se ganha trocando o frasco por LVP

O `SpillLiquidContainer` (841 linhas) mais `LiquidVolumeBaker` (362), `LiquidSurfaceMesh` (441),
`LiquidComposition` (140), `LiquidConfig` (179) e `CalibratedRealisticLiquid.shader` (561) —
**2.524 linhas** — existem para resolver o que o LVP já resolve nativamente:

| Problema | Solução atual | Solução LVP |
|---|---|---|
| Nível por volume ao inclinar | voxelização da cavidade (`LiquidVolumeBaker`) | `rotationLevelCompensation` + `liquidSurfaceYPosition` |
| Camadas imiscíveis | `LiquidComposition` + 1 `MeshRenderer` por fase | `liquidLayers[]` ordenado por densidade, 1 draw |
| Mistura de cor | `LiquidConfig.Mix` | média ponderada nativa (`mixedColor`) |
| Slosh / ondas | mola-amortecida em C# + uniforms | `reactToForces`, `physicsMass`, `turbulence` |
| Superfície livre | mesh gerada em runtime (`LiquidSurfaceMesh`) | raymarch do volume |
| Bico de derrame | `SpoutWorldPoint` do baker | `GetSpillPoint(out pos, out amount)` |
| Bolhas de ebulição | `ParticleSystem` + `ConstrainBubble` na cavidade | `bubblesAmount/Scale/VerticalSpeed/Opacity` por camada |

**Fatos verificados da API do LVP** estão em [SPEC-Camadas.md](../LiquidFX/SPEC-Camadas.md) §2.
Ler antes de tocar em `liquidLayers`. Os dois que mais derrubam implementação:
- `amount` **não é volume**: é volume × densidade (§2.6).
- Duas camadas só misturam com `density` **bit-a-bit igual** — por isso densidade mora na *categoria*, não no líquido (§2.4).
- Em modo Multiple, escrever em `level` é **recusado** com warning; o nível é derivado (§2.5).

### 0.4 O que se aproveita de LiquidFX

`FlaskVolume` (a ser copiado, ver 2.0) já é a ponte mL ↔ LVP e já implementa o que precisamos:
`AddLayeredML`, `RemoveTopML(out LiquidDefinition)`, `IsAbovePort`, `PortCentreWorld`,
`PortRadius`, `SurfaceWorldY`, `TopLiquid`, `EvaluateTiltFlowMLPerSecond`.

Dependências dele: apenas `ILiquidContainer`, `LiquidContainerRegistry`, `LiquidDefinition`,
`LiquidCategory`, `LiquidVolumeFX`. **Nada** do subsistema de VFX que ficou ruim.
Ou seja: dá para apagar todo o VFX de derramamento do LiquidFX sem tocar no que interessa.

---

## Fase 1 — Limpeza (pré-requisito, antes de qualquer feature)

Objetivo: o projeto compila e roda igual ao de hoje, com menos código.
Fazer commit ao fim de cada bloco.

**Status: 1.1, 1.2, 1.3, 1.5 e 1.6 fechadas** no commit `refactor(LabSpillClean): remove dead
code from the fluid path`. Falta só a 1.4, adiada de propósito (ver abaixo).

### 1.1 [S] ✅ Apagar arquivos órfãos (zero referências)

Verificado por busca de GUID em todo o `Assets/` — busca por *nome* não serve, porque `.mat`
referencia shader por GUID.

```
Assets/LabSpillClean/Resources/ComputeVolume.compute
Assets/LabSpillClean/Shaders/LiquidGlass.shader
Assets/LabSpillClean/Shaders/SolidLiquidLOD.shader
Assets/LabSpillClean/Materials/Flask Liquid.mat     <- achado durante a execução
```

`Flask Liquid.mat` não era referenciado por nada e era o **único** usuário do
`LiquidGlass.shader`: o par inteiro era órfão.

Apagar também o `.meta` de cada um. **Não** apagar `SpillBubbleParticle.shader` nem
`SpillSteamParticle.shader`: são carregados por nome em [SpillBurner.cs:313](Scripts/Runtime/SpillBurner.cs:313).

Critério de aceite: Unity recompila sem erro e sem novo warning.

### 1.2 [S] ✅ Remover as sobrecargas mortas de `Append`

Em [FluidBody.cs](Scripts/Core/FluidBody.cs) eram **três** sobrecargas sem chamador, não duas:
a de `uint substanceId` também estava morta. Sobrou só
`Append(Vector3[], Vector3[], Color[], uint[])`.

### 1.3 [M] ✅ Remover o caminho "morrer no chão" (kill colliders)

Apagar, nesta ordem:

1. `Resources/FluidSolver.compute`: kernel `SolveKillColliders` e o buffer `KillColliders`, `KillColliderCount`, `KillClearance`.
2. `Scripts/Core/FluidSolver.cs`: `m_killColliderBuffer`, `m_killColliderCount`, `m_solveKillCollidersKernel`, `SetKillColliders()`, `SolveKillColliders()`, e a chamada dentro de `StepPhysics`. Liberar o buffer no `Dispose` some junto.
3. `Scripts/Runtime/SpillFluidWorld.cs`: `m_groundColliders` e a chamada `SetKillColliders`.

Executado com dois desvios do texto acima:

- **Mais kernel morto que o previsto.** `FluidSolver.compute` caiu de 894 para 554 linhas.
  Além do `SolveKillColliders`, estavam declarados e nunca despachados: `SolveGlassware`,
  `SolveFlaskSDF` (com os structs `Glassware`/`FlaskSDFData` e os helpers de perfil e de
  amostragem do atlas 3D — restos do frasco analítico e do SDF de mesh revertido),
  `DespawnPass` e `KillPlanePass`. Todos removidos.
- **`Graveyard` saiu do shader.** Depois das remoções nenhum kernel lia o uniform. A
  propriedade `FluidSolver.Graveyard` em C# continua, porque o `MarkDead` do
  `SpillFluidWorld` escreve a posição do cemitério pela CPU. A Fase 3.2 reintroduz o uniform
  junto com o kernel de morte por TTL.
- **Chão virou colisor comum**, em vez de sumir da lista. `m_groundColliders` foi absorvido
  por `m_surfaceColliders` (renomeado, já que não contém só bancada) e vai para
  `SetColliders`. Assim a cena não fica sem chão entre a Fase 1 e a Fase 3.

Critério de aceite: derramar no chão deixa a poça parada no chão (ainda sem morte por tempo — isso é a Fase 3). ✅

### 1.4 [M] ⏸ Remover `SpillSurface` e a morte condicionada à bancada — **ADIADA**

Adiada de propósito: executada sozinha, deixa a cena **sem nenhum colisor** até a 3.3 chegar.
Vai em par com a **3.3 (colidir com tudo)**, no mesmo branch.

1. Apagar `Scripts/Runtime/SpillSurface.cs` (+ `.meta`).
2. Em `SpillFluidWorld`: apagar `m_surfaces`, `SpillSurfaceKind`, `IsOnBench()`, `m_benchColliders`, `UploadSurfaceColliders()` e as chamadas.
3. Em `SpillVisualSettings`: apagar `benchLifetimeMin` / `benchLifetimeMax` (serão substituídos por `particleLifetime` na Fase 3).
4. Em `CalculateDomain()`: o domínio passa a vir de um `Bounds` explícito serializado no `SpillFluidWorld` (campo novo `simulationBounds`, default cobrindo bancada + chão), já que não há mais `SpillSurface` para encapsular.
5. Na cena `LabSpillDemo.unity`: remover os componentes `SpillSurface` de `Lab Bench Top` e `Lab Ground`.

**Estado temporário esperado:** entre 1.4 e 3.3 as partículas não colidem com nada e não morrem. Isso é aceitável e some na Fase 3. Não tentar "consertar" no meio.

### 1.5 [S] ✅ Remover o construtor de cena obsoleto

`Assets/LabSpillClean/Editor/LiquidCompositionSceneBuilder.cs` monta a demo de camadas do
sistema antigo. Vai ser substituído na Fase 5. Apagar (+ `.meta`).

### 1.6 ~~Podar o VFX de derramamento do LiquidFX~~ — ✅ **CANCELADA**

`Assets/LiquidFX` é somente-leitura. O VFX ruim continua lá e simplesmente não é usado.
A cópia dos 5 arquivos aproveitáveis virou a tarefa **2.0** (Fase 2).

### 1.7 [S] ✅ Membros mortos em arquivos que sobrevivem à reforma

Varredura de membros públicos sem referência, restrita aos arquivos que **não** serão
reescritos (`GridHash`, `BitonicSort`, `SmoothingKernel`, `FluidBoundary`,
`SpillRenderBridge`, `SpillVisualSettings`). Resultado: só
`SpillRenderBridge.Entry.ParticleMesh` e `Entry.Args` — duplicatas nunca ligadas, já que a
Renderer Feature mantém o próprio `splatArgs`. Removidos.

**Pendência anotada, não executada:** `Entry.FilterBySubstance` é escrito como `true` e nunca
como `false`. É configurabilidade morta, mas remover exige mexer também no shader SSF; fica
para a Fase 3, junto com o resto do caminho de render.

---

## Fase 2 — Biblioteca de líquidos (assets)

### 2.0 [M] Copiar a base do LiquidFX para dentro do LabSpillClean

Copiar, renomear tipo e namespace, sem alterar o original:

| origem (somente-leitura) | destino |
|---|---|
| `LiquidFX/Runtime/Library/LiquidCategory.cs` | `LabSpillClean/Scripts/Liquid/SpillLiquidCategory.cs` |
| `LiquidFX/Runtime/Library/LiquidDefinition.cs` | `LabSpillClean/Scripts/Liquid/SpillLiquidDefinition.cs` |
| `LiquidFX/Runtime/Containers/ILiquidContainer.cs` | `LabSpillClean/Scripts/Liquid/ISpillLiquidContainer.cs` |
| `LiquidFX/Runtime/Containers/LiquidContainerRegistry.cs` | `LabSpillClean/Scripts/Liquid/SpillContainerRegistry.cs` |
| `LiquidFX/Runtime/Containers/FlaskVolume.cs` | `LabSpillClean/Scripts/Liquid/SpillFlaskVolume.cs` |

Regras:
- namespace `LabSpill`; `CreateAssetMenu` vira `menuName = "Lab Spill/..."` para não duplicar
  a entrada de menu do original;
- `using LiquidVolumeFX;` permanece — o LVP continua sendo consumido de onde está;
- a doc de `ISpillLiquidContainer` cita `LiquidSurface` (tipo que não foi copiado): reescrever
  a frase;
- `SpillFlaskVolume` mantém a API inteira (`AddLayeredML`, `RemoveTopML`, `IsAbovePort`,
  `PortCentreWorld`, `PortRadius`, `SurfaceWorldY`, `TopLiquid`, `EvaluateTiltFlowMLPerSecond`).

Copiar também `LiquidFX/Editor/FlaskVolumeEditor.cs`, `LiquidLibraryBuilder.cs` e
`LiquidLibraryValidator.cs` para `LabSpillClean/Editor/`, apontados aos tipos novos.
`LiquidFXPaths.cs` **não** é copiado: os caminhos dele apontam para `LiquidFX/Generated`.

### 2.1 [M] Conversor JSON → ScriptableObject

Script de Editor novo: `Assets/LabSpillClean/Editor/LiquidConfigMigrator.cs`,
menu `Tools > Lab Spill > Migrar JSONs para assets de líquido`.

Entrada: `Assets/LabSpillClean/Configs/{Water,Alcohol,Oil,Default}LiquidConfig.json`
(desserializar com o `LiquidConfig` atual, antes de ele ser apagado na Fase 4).

Saída, em `Assets/LabSpillClean/Liquids/`:

**Categorias** (`LiquidCategory`) — `stackDensity` **tem de ser único por categoria** (SPEC §2.4):

| Asset | stackDensity | origem |
|---|---|---|
| `Cat_Aquoso` | 1.000 | `densityKgPerLiter` da água |
| `Cat_Alcoolico` | 0.789 | idem álcool |
| `Cat_Oleoso` | 0.880 | idem óleo |

Regra: líquidos que **devem** misturar entre si compartilham categoria. Água e álcool são
miscíveis na vida real, mas no LVP mistura ⇒ densidade idêntica ⇒ perderiam a estratificação.
A cena atual tem tanto `Mistura - Agua + Alcool` quanto `Camadas - Agua + Oleo`; portanto
água e álcool ficam na **mesma** categoria (`Cat_Aquoso`, densidade 1.0) e o óleo na sua.
Isso reproduz o comportamento visível hoje.

**Líquidos** (`LiquidDefinition`) — mapeamento campo a campo:

| LiquidDefinition | vem de LiquidConfig |
|---|---|
| `displayName` | `liquidName` |
| `category` | pela tabela acima (via `category` do JSON) |
| `color` | `bodyColor`, com `a` = `Mathf.Clamp01(absorptionDensity / 12f)` |
| `murkColor` | `deepColor` |
| `murkiness` | `turbidity` |
| `scale` | `Mathf.Clamp(waveDetail * 0.48f, 0.001f, 0.48f)` |
| `viscosity` | `Mathf.Clamp01(Mathf.Log10(Max(1, viscosity)) / 3.3f)` (mesma normalização já usada em `_Viscosity01`) |
| `bubblesOpacity` | `bubbleColor.a` |
| `adjustmentSpeed` | 1 |

Os campos térmicos (`boilingPointC`, taxas de bolha/vapor, cores de vapor) **não cabem** no
`LiquidDefinition` do LiquidFX. Ver 2.2.

### 2.2 [M] Estender `LiquidDefinition` com o bloco térmico

Adicionar ao `LiquidDefinition` (é asset nosso, pode crescer):

```csharp
[Header("Térmico")]
[SerializeField] float boilingPointC = 100f;
[SerializeField] Color vaporColor = new Color(0.94f, 0.98f, 1f, 0.18f);
[SerializeField, Min(0f)] float steamRateAtMaximum = 5f;
[SerializeField, Range(0f, 1f)] float steamStartIntensity = 0.65f;
[Header("Jato (SSF)")]
[SerializeField] Color streamColor = new Color(0.16f, 0.68f, 0.69f, 1f);
[SerializeField, Min(0.2f)] float physicalViscosity = 1f;   // mPa·s, usada pela física
```

Motivo de `physicalViscosity` separado de `viscosity`: o campo do LVP é 0–1 estético; a física
do jato precisa do valor real em centipoise (hoje vem de `LiquidConfig.viscosity`).

Preencher no conversor a partir do JSON. Manter os getters públicos no mesmo estilo do arquivo.

### 2.3 [S] Validar a biblioteca

Rodar `LiquidLibraryValidator` (já existe) e confirmar: nenhuma categoria repete `stackDensity`;
todo `LiquidDefinition` tem categoria.

### 2.4 [S] Arquivar os JSONs

Mover `Assets/LabSpillClean/Configs/` → `Assets/LabSpillClean/Configs~/` (o `~` faz o Unity
ignorar a pasta) e apagar os `.meta`. Ficam como referência histórica, fora da build.
Só depois da Fase 4 concluída.

---

## Fase 3 — Física de partículas (o núcleo)

Esta fase é **[C] inteira**. Não delegar nada aqui.

### 3.1 [C] Pool de capacidade fixa

Reescrever `FluidBody` como pool. Contrato novo:

```csharp
public sealed class FluidPool : IDisposable
{
    public int Capacity { get; }            // fixo, = settings.maxParticles, alinhado a 128
    public int AliveCount { get; }          // mantido no CPU pelo free-list
    public ComputeBuffer Positions  { get; }    // float4, Capacity elementos
    public ComputeBuffer Predicted  { get; }    // float4 ×2 (double buffer)
    public ComputeBuffer Velocities { get; }    // float4 ×2
    public ComputeBuffer Densities, Pressures { get; }
    public ComputeBuffer States     { get; }    // 0 = vivo, 1 = morto
    public ComputeBuffer Colors, SubstanceIds { get; }
    public ComputeBuffer DeathTimes { get; }    // float: t absoluto de morte por TTL
    public ComputeBuffer Deaths     { get; }    // AppendStructuredBuffer<uint>, mortes do frame
}
```

Regras invioláveis:
- **Nenhum `new ComputeBuffer` depois do `Awake`.** Alocar tudo uma vez em `Capacity`.
- **Nenhum `GetData` síncrono em runtime.** Só `AsyncGPUReadback`.
- `FluidSolver`, `GridHash` e `BitonicSort` são construídos **uma vez** para `Capacity` e nunca
  recriados (`BitonicSort` já quer potência de dois — arredondar `Capacity` para cima).
- Todo slot nasce morto (`State = 1`) parqueado no `Graveyard`.

Apagar: `Append`, `CompactDead`, `ParticleSource`, `ParticlesFromList`, `CBUtility`
(este só serve aos `Release` que somem junto).

### 3.2 [C] Nascimento e morte sem realloc

**Free-list no CPU:** `Stack<int> m_freeSlots`, preenchido com `0..Capacity-1` no início.

**Nascimento** — novo kernel `SpawnParticles` no `FluidSolver.compute`:

```hlsl
struct SpawnRecord { float3 position; float3 velocity; float4 color; uint substance; float deathTime; uint slot; };
StructuredBuffer<SpawnRecord> Spawns;
uint SpawnCount;
// escreve Positions[slot], Predicted[0/1][slot], Velocities[0/1][slot],
// Colors[slot], SubstanceIds[slot], DeathTimes[slot], States[slot] = 0
```

O CPU só faz `SetData` num buffer de spawn de tamanho fixo (ex.: 256 registros) e um `Dispatch`.
Zero readback, zero realloc. `emissionFlushInterval` pode ir a zero — o flush passa a ser barato.

**Morte** — dentro de `UpdatePositions`, cada partícula viva testa `Time >= DeathTimes[i]`;
se sim: `States[i] = 1`, posição = `Graveyard`, velocidade = 0, e `Deaths.Append(i)`.

O CPU lê `Deaths` por `AsyncGPUReadback` (com o contador via `ComputeBuffer.CopyCount`),
devolve os índices ao `m_freeSlots` e decrementa `AliveCount`. Um frame de latência é irrelevante.

Isso implementa "some depois de um tempo", em qualquer lugar, sem `IsOnBench` e sem kill collider.

### 3.3 [C] Colidir com tudo

Novo `Scripts/Runtime/SpillColliderProvider.cs`:

- `Physics.OverlapBox(simulationBounds.center, extents, ..., ~0, QueryTriggerInteraction.Ignore)`
  a cada `colliderRefreshInterval` (default 0.5 s) **e** sempre que `SetDirty()` for chamado.
- Converte para `FluidSolver.ColliderGPU` (a conversão já existe em
  [SpillFluidWorld.cs:626](Scripts/Runtime/SpillFluidWorld.cs:626) — mover para cá):
  - `SphereCollider`, `BoxCollider`, `CapsuleCollider` → primitiva analítica exata;
  - `MeshCollider` **convexo** → OBB do `bounds` local (aproximação aceitável para caixote/prop);
  - `MeshCollider` **não-convexo** → lista de caixas pré-decomposta. O projeto já tem
    `Assets/UnityNonConvexMeshColliders-main`; usar o decompositor dele em modo Editor e
    guardar o resultado num componente `SpillMeshColliderBoxes` (array de OBBs serializado).
    Sem decomposição gravada, o objeto é **ignorado** e um warning nomeia o GameObject.
- Buffer de colisores com **capacidade fixa** (`maxColliders`, default 64); só `SetData`, nunca realocar.
- Componente `SpillColliderExclude` (marcador vazio): o provider pula qualquer collider que o tenha.

**Vidraria:** cada frasco recebe `SpillColliderExclude`. Motivo: a partícula que acerta a boca
tem de ser capturada pelo porto (3.5), e uma casca de vidro aproximada por OBB fecharia a boca.
Gota que erra o frasco simplesmente passa ao lado e cai na bancada — que é um colisor real.
Se depois quiser respingo no vidro externo, é `SpillMeshColliderBoxes` no frasco, não código novo.

### 3.4 [C] Qualidade do jato

Com o custo de emissão eliminado, o orçamento vai para a simulação:

1. **Iterações**: `solverIterations` 1→2, `constraintIterations` 1→3 nos defaults do
   `SpillVisualSettings`. Reavaliar com profiler.
2. **Tensão superficial + coesão (Akinci)**: dois termos novos no `SolveViscosity` (ou kernel
   próprio `SolveCohesion`). É o que transforma "pipoca de esferas" em filete contínuo que
   se quebra em gotas. Parâmetros novos: `cohesion` (default 0.35), `surfaceTensionGamma` (0.2).
   Escalar por `LiquidDefinition.physicalViscosity`.
3. **XSPH** no lugar do damping viscoso puro para o jato em voo, mantendo `RestDamping` para o
   repouso (o repouso adaptativo atual funciona — preservar).
4. **Emissão determinística**: `QueueJet` hoje faz rejeição aleatória O(n²) com até 12×24
   tentativas por partícula ([SpillFluidWorld.cs:290](Scripts/Runtime/SpillFluidWorld.cs:290)).
   Trocar por disco hexagonal pré-computado: anéis de raio `k·2r` no plano da boca, avançados ao
   longo do eixo do jato por `velocidade × tempoDesdeOÚltimoSpawn`. Sem sorteio, sem colisão de
   spawn, e o espaçamento sai do próprio passo de tempo.
5. **Substepping**: `maxPhysicsStepsPerFrame` passa a ser calculado do CFL real
   (`ceil(velMax·dt / (0.4·raio))`), com teto configurável.

### 3.5 [M] Captura no frasco receptor

Substituir `TryFindReceiver` ([SpillFluidWorld.cs:436](Scripts/Runtime/SpillFluidWorld.cs:436)),
que hoje varre `SpillLiquidContainer[]`, por consulta ao `SpillContainerRegistry`:

```csharp
ISpillLiquidContainer receiver = SpillContainerRegistry.FindReceiverUnder(particlePos, fromY);
```

**Preservar** duas coisas boas do código atual, que o registro não tem:
- o teste de **travessia de segmento** (posição anterior → atual cruzando o disco da boca),
  porque com readback assíncrono a amostragem é ainda mais esparsa que os 0,1 s de hoje;
- a folga do raio visual do SSF (`visualRadiusScale`), para a gota que *parece* ter entrado.

Implementar como método novo no `SpillFluidWorld` que usa `SpillFlaskVolume.PortCentreWorld` /
`PortRadius` em vez do `TryGetOpening` do baker.

Ao capturar: `flask.AddLayeredML(mlPorPartícula, definiçãoDaSubstância)` e liberar o slot.

### 3.6 [S] Sanear `SpillVisualSettings`

Depois de 3.1–3.5:
- **remover**: `benchLifetimeMin`, `benchLifetimeMax`;
- **adicionar**: `particleLifetimeMin/Max` (default 8 / 20 s), `cohesion`, `surfaceTensionGamma`,
  `colliderRefreshInterval`, `maxColliders`;
- **atualizar tooltips** que citam bancada/chão.

### 3.7 [M] Critérios de aceite da Fase 3

Medir com o Profiler, cena `LabSpillDemo`, derramando 250 mL:
1. `SpillFluidWorld.Update` **sem picos** de GPU readback (hoje há um a cada 25 ms).
2. Zero alocação de `ComputeBuffer` depois do primeiro frame (Memory Profiler).
3. O jato cai como filete contínuo, não como colar de contas.
4. Partícula que cai fora some entre `particleLifetimeMin` e `Max`, **em qualquer superfície**.
5. Partícula colide com bancada, chão, pernas da bancada e com o bico de Bunsen — sem nenhum
   componente marcador na cena.
6. Volume conservado: 250 mL saindo = 250 mL chegando (±1 partícula).

---

## Fase 4 — Frasco em LVP + FlaskVolume

### 4.1 [C] `SpillPourEmitter` vira a ponte LVP ↔ partículas

Reescrever `Scripts/Liquid/SpillPourEmitter.cs`. Fonte passa a ser `SpillFlaskVolume` + `LiquidVolume`:

```
por frame, se lv.GetSpillPoint(out spillPos, out spillAmount):
    ml     = min(flask.ContentsML, taxa(spillAmount, tilt) * dt)
    saiu   = flask.RemoveTopML(ml, out LiquidDefinition topo)     // débito primeiro
    idx    = world.RegisterLiquid(topo)                            // 1× por definição
    world.QueueJet(spillPos, velocidadeDeSaída, saiu / mlPorPartícula, idx, raio, normal)
```

Notas:
- `GetSpillPoint` é geométrico sobre a mesh do LVP — melhor que o `SpoutWorldPoint` do baker.
- Cor do jato: `LiquidDefinition.streamColor`; se quiser acompanhar a transição visual do
  frasco, ler `layer.currentColor` (SPEC §2.9) — **`currentColor`, nunca `mixedColor`**.
- A ordem "debita primeiro, emite depois" inverte a atual, que emitia e só debitava o aceito.
  Com pool de capacidade fixa, `QueueJet` pode recusar por lotação: nesse caso **devolver**
  o excedente com `flask.AddLayeredML`. Só assim mL e partículas continuam iguais.
- Velocidade de saída: `sqrt(2·g·head)·0.6` (fórmula atual, funciona) + velocidade do
  `Rigidbody` no ponto de derrame.

### 4.2 [M] Migrar `SpillBurner` para LVP

Trocar o alvo `SpillLiquidContainer` por `SpillFlaskVolume`. Mapeamento:

| hoje | passa a ser |
|---|---|
| `liquid.currentVolumeML` | `flask.ContentsML` |
| `liquid.BoilingPointC` | `flask.TopLiquid.BoilingPointC` |
| `liquid.Config` | `flask.TopLiquid` (`LiquidDefinition`) |
| `liquid.SurfacePlane` / `DistanceToSurfaceAlong` | `lv.liquidSurfaceYPosition` |
| `liquid.TryGetOpening` | `flask.PortCentreWorld` / `flask.PortRadius` |
| `liquid.BubbleOriginWorld` + `ConstrainBubble` + `ParticleSystem` de bolhas | **apagar**: bolhas nativas do LVP |
| `liquid.SetBoilingIntensity` | escreve nos parâmetros de bolha do LVP |
| `liquid.VaporColor` + `ParticleSystem` de vapor | mantém (vapor é fora do vidro) |

Ebulição escrevendo no LVP, com `intensity01` = mesma curva de hoje:

```csharp
lv.bubblesAmount        = Mathf.RoundToInt(Mathf.Lerp(0, 120, intensity01));
lv.bubblesVerticalSpeed = Mathf.Lerp(0.02f, 0.20f, intensity01);
lv.bubblesSizeMax       = Mathf.RoundToInt(Mathf.Lerp(2, 9, intensity01));
lv.turbulence1          = Mathf.Lerp(base1, base1 * 3f, intensity01);
lv.requireBubblesUpdate = true;
```

Isso apaga `ConstrainBubbles`, `UpdateBubbles` e o `SpillBubbleParticle.shader`
— ~150 linhas e um ParticleSystem por frasco. `UpdateSteam` fica.

### 4.3 [C] Apagar o sistema de frasco antigo

Só depois de 4.1 e 4.2 rodarem. Apagar (+ `.meta`):

```
Scripts/Liquid/SpillLiquidContainer.cs      (841)
Scripts/Liquid/LiquidVolumeBaker.cs         (362)
Scripts/Liquid/LiquidSurfaceMesh.cs         (441)
Scripts/Liquid/LiquidComposition.cs         (140)
Scripts/Liquid/LiquidConfig.cs              (179)
Shaders/CalibratedRealisticLiquid.shader    (561)
Shaders/SpillBubbleParticle.shader           (80)
Materials/CalibratedRealisticLiquid.mat
Materials/Flask Liquid.mat
```

Em `SpillFluidWorld`, a classe interna `Liquid` passa a guardar `LiquidDefinition` no lugar de
`LiquidConfig`, e `ApplyConfig(Material, LiquidConfig)` vira `ApplyDefinition(Material, LiquidDefinition)`
— **o material SSF do jato continua sendo `PBDFluidSSFSurface`**, não muda.

### 4.4 [M] Registrar o depth prepass do LVP

`Assets/LabSpillClean/Settings/PC_Renderer.asset` tem hoje **uma** feature (o SSF). O LVP precisa
de `LiquidVolumeDepthPrePassRenderFeature` quando `depthAwareCustomPass` estiver ligado.

Adicionar a feature **depois** do SSF na lista e validar a ordem visualmente: o jato SSF tem de
ficar corretamente ocluído pelo vidro e pelo líquido do frasco. Se houver briga de profundidade,
o ajuste é `doubleSidedBias` / `backDepthBias` no `LiquidVolume`, não código.

---

## Fase 5 — Cena e validação

### 5.1 [M] Reconstruir os frascos da cena

Para cada um de `Frasco - Agua`, `Frasco - Alcool`, `Frasco - Oleo`,
`Camadas - Agua + Oleo`, `Camadas - Alcool + Oleo`, `Mistura - Agua + Alcool`,
`Mistura e Camadas - Agua + Alcool + Oleo`:

1. Remover `SpillLiquidContainer` e o GameObject filho de líquido.
2. Adicionar `LiquidVolume` ao objeto do vidro. `detail = MultipleNoFlask`
   (SPEC §2.7: os prefabs do projeto usam `DefaultNoFlask`; preservar o sufixo `NoFlask`).
3. Conferir a mesh: LVP quer pivô centrado e mesh fechada — usar `CenterPivot()` e
   `autoCloseMesh` do próprio inspector do LVP se o `flask.fbx` estiver aberto no gargalo.
4. Adicionar `SpillFlaskVolume`: `capacityML = 250`, preencher `initialContents` com os
   `LiquidDefinition` da Fase 2 e as mesmas mL de hoje, ajustar `portRadius` ao gargalo real.
5. Adicionar `SpillColliderExclude` (Fase 3.3).
6. `SpillPourEmitter` continua no pai, agora apontando para o `SpillFlaskVolume`.

Os nomes das configurações de teste da cena (mistura vs camadas) já descrevem o resultado
esperado; usar como checklist visual.

### 5.2 [S] Construtor de cena novo

Substituir o `LiquidCompositionSceneBuilder` apagado em 1.5 por
`Assets/LabSpillClean/Editor/LabSceneBuilder.cs`, menu `Tools > Lab Spill > Reconstruir demo`,
que monta a bancada + N frascos com o setup de 5.1 a partir dos assets de líquido.
Tarefa mecânica depois que 5.1 estiver validado à mão — é a receita de 5.1 em código.

### 5.3 [S] Reescrever `LabSpillClean/README.md`

Refletir a arquitetura nova: LVP+FlaskVolume no frasco, pool PBD no jato, colisão com tudo,
morte por tempo. Apagar a seção sobre `Assets/PBDFluid` (a pasta não existe mais no projeto).

### 5.4 [M] Validação final

| # | Teste | Esperado |
|---|---|---|
| 1 | Inclinar frasco de água até derramar | filete contínuo saindo do bico geométrico do LVP |
| 2 | Derramar água em frasco com óleo | água entra por baixo, óleo sobe — sem mistura |
| 3 | Derramar álcool em frasco com água | mistura, cor média, uma camada só |
| 4 | Derramar errando o alvo | poça na bancada; some sozinha entre 8 e 20 s |
| 5 | Derramar do alto da bancada | colide com a perna da bancada no caminho |
| 6 | Bunsen aceso sob frasco cheio | bolhas nativas do LVP + vapor acima da boca |
| 7 | Encher até `capacityML` | emissor recusa, nível não passa do gargalo |
| 8 | Esvaziar completamente | `ContentsML == 0`, `LiquidVolume` some, sem partícula órfã |
| 9 | 60 s derramando sem parar | memória estável, sem crescimento de `ComputeBuffer` |

---

## Ordem de execução e paralelismo

```
Fase 1 (limpeza)  ──┬─→ Fase 2 (assets)  ──┐
                    │                       ├─→ Fase 4 (frasco) ─→ Fase 5 (cena)
                    └─→ Fase 3 (física) ────┘
```

Fases 2 e 3 são independentes entre si e podem ir em paralelo. A Fase 4 depende das duas.

**Distribuição sugerida**
- Modelo menor **[S]**: 1.1, 1.2, 1.5, 1.6 (+ os 3 ajustes de texto), 2.3, 2.4, 3.6, 5.2, 5.3.
- Modelo principal **[M]/[C]**: 1.3, 1.4, 2.1, 2.2, 3.1–3.5, 3.7, 4.1–4.4, 5.1, 5.4.

Commit por bloco. A Fase 3 fica em branch própria — é a única que pode deixar a cena
temporariamente sem física enquanto o pool não fecha.

---

## Riscos conhecidos

| Risco | Sinal | Mitigação |
|---|---|---|
| Mesh do `flask.fbx` aberta no gargalo quebra o raymarch do LVP | líquido vazando visualmente pelo topo | `autoCloseMesh` do LVP; se falhar, `upperLimit` abaixo da borda |
| Duas categorias com `stackDensity` igual | óleo mistura com água | `LiquidLibraryValidator` (2.3) barra antes de rodar |
| Ordem das render features (SSF × LVP prepass) | jato desaparece atrás do vidro, ou vice-versa | 4.4; ajustar por `doubleSidedBias`, não por código |
| `AsyncGPUReadback` atrasa a captura em 1–2 frames | gota some dentro do frasco sem creditar mL | teste de travessia de segmento em 3.5 cobre |
| Decomposição de mesh não-convexa ausente num prop | partícula atravessa o objeto | warning nomeando o GameObject; falha visível, não silenciosa |
| LVP é raymarch — custo por frasco na tela | queda de fps com muitos frascos | `liquidRaySteps` / `foamRaySteps`; `detail` menor nos frascos de fundo |
```

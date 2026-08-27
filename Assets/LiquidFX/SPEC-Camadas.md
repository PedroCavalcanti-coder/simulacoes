# LiquidFX × LiquidVolumePro — Sistema de Camadas por Asset

Spec de implementação. Escrito para ser executado direto, sem re-investigar a API do
LiquidVolumePro (LVP). Todos os fatos da seção 2 foram verificados lendo o código-fonte;
os números de linha são de `Assets/LiquidVolumePro/Scripts/LiquidVolume.cs`.

---

## 1. Objetivo

Três entregas, nesta ordem de dependência:

1. **Líquidos viram assets** (`ScriptableObject`), como itens de RPG. Hoje uma camada é
   configurada campo a campo no inspector do `LiquidVolume` (amount/density/color/miscible/
   murkiness/murkColor/scale/viscosity/adjustmentSpeed/bubblesOpacity — 10 campos por camada,
   8 camadas na tela do usuário). Isso não escala e não é replicável entre frascos.
2. **Mistura por categoria.** Cada líquido pertence a uma categoria (Aquoso, Oleoso, …).
   Mesma categoria ⇒ misturam. Categorias diferentes ⇒ estratificam.
3. **Despejo respeita as camadas.** O líquido que sai do bico é o da camada do topo, com a cor
   do topo, e vai drenando de cima para baixo. Ao cair num recipiente que também é em camadas,
   empilha nova camada — ou se funde à existente quando for da mesma categoria.

---

## 2. Fatos verificados da API LVP

Ler esta seção antes de escrever qualquer linha. Vários itens são contraintuitivos.

### 2.1 Estrutura

- `LiquidVolume.MAX_LAYERS = 16` (linha 81). Passar disso faz `Array.Resize` + log (3106-3109).
- `LiquidVolume.liquidLayers` é `LiquidLayer[]` público (218). O struct `LiquidLayer` (83-152)
  tem campos públicos mutáveis: `amount`, `density`, `color`, `miscible`, `murkiness`,
  `murkColor`, `scale`, `viscosity`, `adjustmentSpeed`, `bubblesOpacity`.
- Campos calculados pelo LVP, também públicos: `mixedColor`, `mixedColor2`, `mixedAmount`,
  `mixedScale`, `mixedMurkiness`, `mixedBubblesOpacity`, `baseLevel` — e os animados
  `currentColor`, `currentColor2`, `currentAmount`, `currentBaseLevel`, `currentMurkiness`,
  `currentBubblesOpacity`.

### 2.2 Padrão de mutação

O demo oficial do próprio asset (`Demos/Multiple Layers/.../BeakerController.cs`) muta os campos
**in-place** no array vivo e chama `UpdateLayers(true)`:

```csharp
lv.liquidLayers[0].amount += 0.01f;
lv.UpdateLayers(true);
```

**Nunca reatribuir um array novo maior.** Em `UpdateLayersNow` (3111-3115), qualquer slot novo
além do `lastLayerCount` anterior recebe `SetDefaults(k)` — que gera **cor aleatória**
(`new Color(Random.value, Random.value, Random.value, 0.5f)`, linha 133). Alocar o array uma vez
com `MAX_LAYERS` slots e gerenciar quais estão ativos por conta própria.

`UpdateLayers(immediate: true)` (3090-3098) força `RenderObject()` no mesmo frame — usar `true`
sempre que a mudança precisar aparecer já (bake no editor, transferência de volume).

### 2.3 Empilhamento é por densidade — **maior densidade fica embaixo**

Em `UpdateLayersNow` (3133-3156): o loop escolhe repetidamente a **maior** densidade restante e
lhe atribui o `baseLevel` mais baixo, acumulando `baseLevel += amount / density`.

⇒ densidade **menor** flutua no topo. Óleo (0.88) fica sobre água (1.0). Correto fisicamente.

Empate de densidade: os empatados entram na ordem do **array**, não por outro critério.

### 2.4 Mistura exige densidade **exatamente** igual — este é o ponto crítico

O agrupamento de miscíveis (3158-3197) usa:

```csharp
while (_liquidLayers[i].miscible && _liquidLayers[i].density == groupDensity)
```

Comparação de igualdade exata de `float`, sobre a ordem já ordenada. Ou seja: **duas camadas só
se misturam se `miscible == true` em ambas E `density` for bit-a-bit idêntico.**

**Consequência de projeto (invalida o desenho anterior):** não dá para dar a cada líquido uma
densidade própria *e* garantir mistura por categoria. A densidade tem de ser propriedade **da
categoria**, não do líquido. É por isso que `LiquidCategory` carrega `stackDensity` e
`LiquidDefinition` **não tem** campo de densidade.

O LVP calcula, para o grupo misturado, a média ponderada por `amount` de cor, murkColor,
murkiness, scale e bubblesOpacity (3167-3187) — ou seja, **a mistura visual é nativa e de graça**.
Nada precisa ser interpolado do nosso lado.

Para camadas não-miscíveis, `mixedColor = color` etc. (3190-3195) — o campo `mixed*` é sempre
válido para leitura, independentemente de mistura.

### 2.5 Em modo Multiple, `level` é derivado — não escrever nele

- O setter `level` (185-197) **recusa** a escrita quando `detail.isMultiple()`, emitindo
  `Debug.LogWarning` e retornando. Escrever todo frame = spam de console.
- Em compensação, `_level` é **calculado automaticamente** a partir das camadas em
  `UpdateLayersProperties` (3363-3379): `level = max(currentBaseLevel + currentAmount)`, seguido de
  `UpdateLevels()`.
- ⇒ `liquidSurfaceYPosition` (2765) e `GetSpillPoint` (2843) **continuam funcionando** em modo
  Multiple. `FlaskVolume.SurfaceWorldY` não precisa de mudança alguma.

### 2.6 A contribuição de uma camada para o nível é `amount / density`

De 3153 (`baseLevel += amount/density`) e 3195 (`mixedAmount = Clamp01(amount/density)`).

⇒ `amount` **não** é volume. É volume × densidade. A conversão de mL tem de multiplicar pela
densidade (ver §5).

### 2.7 Enums de detalhe

`DETAIL` (32-39): `Simple=0`, `SimpleNoFlask=1`, `Default=10`, `DefaultNoFlask=11`,
`Multiple=50`, `MultipleNoFlask=51`. `isMultiple()` ⇔ 50 ou 51.

**Todos os prefabs de química do projeto estão em `_detail: 11` (DefaultNoFlask)** — verificado
nos 45 prefabs de `Assets/LiquidVolumePro/Prefabs/`. Nenhum usa Multiple hoje. Isto é feature
nova, não conserto.

Mapeamento ao ligar o modo camadas: `Simple|Default → Multiple`;
`SimpleNoFlask|DefaultNoFlask → MultipleNoFlask`. Preservar o sufixo NoFlask.

`allowsRefraction()` (59-61) já é `false` para `DefaultNoFlask` **e** para `MultipleNoFlask` ⇒
nenhuma regressão de refração na migração dos frascos atuais.

### 2.8 Clamps aplicados pelo LVP a cada update (3123-3131)

`amount = Max(0, amount)`; `scale ∈ [0.001, 0.48]`; `murkiness ∈ [0,1]`;
`adjustmentSpeed <= 0 → 1`; `density < 0.001 → 0.001`.

Os ranges dos assets devem espelhar isso para o artista não autorar valor que será silenciosamente
alterado.

### 2.9 `current*` vs `mixed*`

`AnimateLayers` (3216-3304) interpola `current*` em direção a `mixed*` a
`Time.deltaTime * layersAdjustmentSpeed * layer.adjustmentSpeed`. Fora do Play mode, copia direto.

⇒ Para a cor do jato bater com o que está **na tela naquele instante**, ler `currentColor`.
Ler `mixedColor` faria o jato saltar para a cor final antes do frasco terminar a transição.

---

## 3. Novos assets

### 3.1 `LiquidCategory` (ScriptableObject)

`Assets/LiquidFX/Runtime/Library/LiquidCategory.cs`

```csharp
[CreateAssetMenu(menuName = "LiquidFX/Liquid Category", fileName = "Cat_")]
public sealed class LiquidCategory : ScriptableObject
{
    [SerializeField] string displayName = "Nova Categoria";

    [Tooltip("Densidade de empilhamento. MENOR flutua no topo. Também é a chave de mistura: " +
             "dois líquidos só se misturam se as categorias tiverem exatamente esta mesma " +
             "densidade, por isso duas categorias NUNCA podem compartilhar o valor.")]
    [SerializeField, Min(0.001f)] float stackDensity = 1f;

    [Tooltip("Cor só para leitura no inspector e nos gizmos. Não afeta o render.")]
    [SerializeField] Color editorTint = Color.cyan;

    public string DisplayName => displayName;
    public float StackDensity => Mathf.Max(0.001f, stackDensity);
    public Color EditorTint => editorTint;
}
```

Regra de ouro, a ser garantida pelo validador (§8.3): **duas categorias distintas não podem ter
`stackDensity` igual.** Se tiverem, o LVP as trataria como miscíveis entre si (§2.4) e o óleo
misturaria com a água.

### 3.2 `LiquidDefinition` (ScriptableObject) — o "arquivo de item"

`Assets/LiquidFX/Runtime/Library/LiquidDefinition.cs`

```csharp
[CreateAssetMenu(menuName = "LiquidFX/Liquid", fileName = "Liq_")]
public sealed class LiquidDefinition : ScriptableObject
{
    [Header("Identidade")]
    [SerializeField] string displayName = "Novo Líquido";
    [SerializeField] LiquidCategory category;

    [Header("Aparência (mapeia 1:1 para LiquidVolume.LiquidLayer)")]
    [Tooltip("Alpha = força de absorção volumétrica, NÃO opacidade de superfície.")]
    [SerializeField] Color color = new Color(0.3f, 0.7f, 1f, 0.35f);
    [SerializeField] Color murkColor = Color.black;
    [SerializeField, Range(0f, 1f)] float murkiness = 0.4f;
    [SerializeField, Range(0.001f, 0.48f)] float scale = 0.3f;
    [SerializeField, Range(0f, 1f)] float viscosity = 1f;
    [SerializeField, Range(0f, 1f)] float bubblesOpacity = 0.5f;
    [SerializeField, Range(0.001f, 10f)] float adjustmentSpeed = 1f;

    public string DisplayName => displayName;
    public LiquidCategory Category => category;
    public Color Color => color;
    public float Density => category != null ? category.StackDensity : 1f;

    /// Escreve os campos de aparência num slot de camada do LVP. Não toca em `amount`:
    /// volume é responsabilidade de quem chama.
    public void ApplyTo(ref LiquidVolume.LiquidLayer layer)
    {
        layer.density = Density;
        layer.miscible = true;   // ver §4
        layer.color = color;
        layer.murkColor = murkColor;
        layer.murkiness = murkiness;
        layer.scale = scale;
        layer.viscosity = viscosity;
        layer.bubblesOpacity = bubblesOpacity;
        layer.adjustmentSpeed = adjustmentSpeed;
        layer.layerName = displayName;
    }
}
```

Repare: **sem campo de densidade.** Vem da categoria, pelo motivo de §2.4. Um `LiquidDefinition`
sem categoria deve falhar na validação (§8.3), não cair num default silencioso.

---

## 4. Regra de mistura

`miscible` é sempre `true` em todo slot que escrevemos. Não é um bug — é o mecanismo:

- Mesma categoria ⇒ mesma `stackDensity` ⇒ o LVP agrupa e mistura (§2.4). ✔ Requisito do usuário.
- Categoria diferente ⇒ `stackDensity` diferente ⇒ a condição `density == groupDensity` falha ⇒
  não misturam, estratificam. ✔

Ou seja, a miscibilidade fica inteiramente codificada na igualdade de densidade. `miscible=false`
nunca é necessário — e usá-lo quebraria a mistura dentro da mesma categoria.

"Um líquido que não mistura com nada" ⇒ dar a ele uma categoria só sua.

---

## 5. Calibração mL ↔ `amount`

Derivação (de §2.6):

- Nível normalizado total = Σ (`amount_k` / `density_k`).
- Queremos: frasco cheio (`capacityML`) ⇒ nível total = `layeredFullLevel` (novo campo, default
  `0.92`, mesmo valor do `fullLevel` atual).

Logo, por camada:

```
amount_k = mL_k · density_k · layeredFullLevel / capacityML
mL_k     = amount_k · capacityML / (density_k · layeredFullLevel)
```

Implementar como dois helpers privados em `FlaskVolume`, e usá-los em **todos** os pontos de
conversão — nenhum cálculo inline duplicado.

**`emptyLevel` é ignorado em modo camadas.** No modo single ele existe porque as malhas do LVP não
chegam a zero; no modo Multiple as camadas empilham a partir de 0 naturalmente. Com o default de
0.02 (2%) a diferença é invisível. Se algum dia importar, a solução é uma camada-pedestal
invisível (alpha 0) no fundo — **não** implementar agora.

---

## 6. Mudanças em `FlaskVolume`

`Assets/LiquidFX/Runtime/Containers/FlaskVolume.cs`

O componente passa a operar em dois modos, decididos por `Volume.detail.isMultiple()`.
**Modo single = comportamento atual, byte por byte.** Zero regressão nas 3 cenas existentes
(`SinkFaucet`, `FlaskPour`, `FlaskFloorSpill`).

### 6.1 Novo estado serializado

```csharp
[System.Serializable]
public struct LayerCharge
{
    public LiquidDefinition liquid;
    [Min(0f)] public float millilitres;
}

[Header("Camadas")]
[Tooltip("Conteúdo inicial, de baixo para cima. A ordem real de empilhamento é decidida pela " +
         "densidade da categoria de cada líquido, não por esta lista.")]
[SerializeField] List<LayerCharge> initialContents = new List<LayerCharge>();

[Tooltip("Nível normalizado que corresponde ao frasco cheio, em modo camadas.")]
[SerializeField, Range(0.5f, 1f)] float layeredFullLevel = 0.92f;
```

### 6.2 Novo estado de runtime

```csharp
LiquidDefinition[] slotLiquid;   // MAX_LAYERS, paralelo a Volume.liquidLayers
bool layeredMode;                // cache de Volume.detail.isMultiple()
```

Slot livre ⇔ `slotLiquid[i] == null`. Slots livres ficam com `amount = 0`, `miscible = false` e
uma densidade-sentinela única e alta (ex.: `1000f + i`) para nunca entrarem num grupo de mistura
nem alterarem o topo. Como `amount = 0`, não têm espessura e são invisíveis.

### 6.3 Guardas obrigatórias no código existente

Estes três pontos **quebram** em modo camadas se não forem guardados:

| Método | Problema | Correção |
|---|---|---|
| `PushLevelToShader()` | Escreve `Volume.level` ⇒ `LogWarning` todo frame (§2.5) | `if (layeredMode) return;` no topo |
| `Initialise()` | `contentsML = NormalisedToML(Volume.level)` está errado em camadas | Em modo camadas, somar os mL das camadas |
| `AddML(mL, Color)` | Não sabe de qual líquido é a cor | Em modo camadas, redirecionar (§6.6) |

### 6.4 Propriedade de topo

```csharp
/// Índice do slot da camada mais alta com volume, ou -1. Topo = MENOR densidade
/// com amount > 0 (§2.3).
int TopSlotIndex { get; }

/// O líquido exposto na superfície. É ele que sai ao inclinar o frasco.
public LiquidDefinition TopLiquid { get; }
```

`LiquidColor` (já existe, consumido pelo jato/partículas/poça) passa a:

```csharp
public Color LiquidColor => layeredMode ? TopLayerColor : Volume.liquidColor1;
```

`TopLayerColor` lê `liquidLayers[TopSlotIndex].currentColor` (§2.9 — `current`, não `mixed`),
com fallback para `mixedColor` e depois `color` se ainda estiver zerado no primeiro frame.

Isso já faz o jato, o crown, os droplets, o ring, o splash e a poça pegarem a cor certa **de
graça**: todos leem `LiquidColor` a cada frame. Nenhuma mudança nesses sistemas.

`TemperFlaskColor` no `LiquidPourController` continua como está — ele converte alpha-de-absorção
em tinta plana e isso vale igual para a cor de uma camada.

### 6.5 Saída: drenar do topo

```csharp
/// Remove até `millilitres` da camada do topo (ou do grupo miscível do topo).
/// Retorna quanto saiu e qual líquido era. Nunca atravessa para a camada de baixo:
/// quem chama repete até satisfazer o pedido.
public float RemoveTopML(float millilitres, out LiquidDefinition liquid);
```

Regras:
- Grupo miscível no topo (várias definições, mesma categoria): remover **proporcionalmente** de
  todos os slots do grupo. Retornar como `liquid` o de **maior** volume no grupo (é o que domina
  a cor). Sem isso, a cor do jato saltaria ao esvaziar um dos componentes da mistura.
- Slot que chega a `amount ≈ 0` (tolerância `1e-5`): liberar — `slotLiquid[i] = null`, aplicar a
  densidade-sentinela.
- Uma única chamada `Volume.UpdateLayers(true)` no fim, nunca uma por slot.

`RemoveML(float)` (da interface `ILiquidContainer`) em modo camadas: laço sobre `RemoveTopML` até
completar ou esvaziar, descartando a identidade. Mantém a interface funcionando para quem não se
importa com qual líquido saiu.

### 6.6 Entrada: empilhar ou fundir

```csharp
/// Adiciona líquido identificado. Funde na camada existente quando for da mesma categoria
/// (mistura nativa do LVP), senão ocupa um slot novo. Retorna quanto foi aceito.
public float AddLayeredML(float millilitres, LiquidDefinition liquid);
```

Ordem de decisão:

1. `liquid == null` ou `!layeredMode` ⇒ cair no `AddML(mL, color)` atual.
2. Existe slot com **exatamente esta** `LiquidDefinition`? ⇒ somar `amount` nele. (Impede que
   despejar A→B→A consuma três slots.)
3. Existe slot cuja definição tenha a **mesma categoria**? ⇒ ocupar um slot novo com este
   líquido; o LVP funde os dois visualmente sozinho, por densidade igual (§2.4), preservando o
   rastreio de volume por definição.
4. Slot livre? ⇒ ocupar, `ApplyTo`, `amount` conforme §5.
5. Sem slot livre (16 ocupados) ⇒ fundir no slot de **menor volume**, mantendo a definição dele,
   e emitir `Debug.LogWarning` **uma única vez** por componente. Volume é conservado; identidade
   do traço mais irrelevante é perdida. Nenhum cenário do projeto chega perto de 16.

`FreeML` em modo camadas = `capacityML − Σ mL`. Igual ao atual, com a soma vindo das camadas.

### 6.7 Bake do conteúdo inicial

```csharp
/// Escreve `initialContents` nas camadas do LVP. Roda em edit mode para o artista ver
/// o resultado sem entrar em Play.
public void BakeInitialContents();
```

- Aloca `Volume.liquidLayers` com exatamente `MAX_LAYERS` slots **uma vez** (§2.2).
- Preenche os primeiros N a partir de `initialContents`, o resto como slots livres.
- Comuta `Volume.detail` para a variante Multiple correspondente (§2.7) se ainda não estiver.
- `Volume.UpdateLayers(true)` no fim.
- Chamar de `OnValidate` (com `EditorApplication.delayCall` para não mutar assets durante
  `OnValidate` — Unity reclama) e de `Initialise()` quando `Application.isPlaying`.

---

## 7. Mudanças no transporte

### 7.1 `LiquidFlightQueue` — pacotes ganham identidade

`Assets/LiquidFX/Runtime/Containers/LiquidFlightQueue.cs`

Hoje `Packet` é `{ float Millilitres; float ArrivalTime; }` e `DequeueArrived` devolve **um float
somado**. Com camadas, pacotes em voo podem ser de líquidos diferentes (o frasco virou de camada
no meio do despejo) — somar tudo num float perde a identidade.

Mudanças:

```csharp
struct Packet
{
    public float Millilitres;
    public float ArrivalTime;
    public LiquidDefinition Liquid;   // pode ser null (fonte tipo válvula sem asset)
}

public void Enqueue(float millilitres, float arrivalTime, LiquidDefinition liquid);

/// Pops UM pacote já chegado. Chamador itera até retornar false.
/// Sem alocação e sem perder identidade — que é o motivo de não devolver uma lista.
public bool TryDequeueArrived(float now, out float millilitres, out LiquidDefinition liquid);
```

- A fusão no buffer cheio (`Enqueue`, caso `count == packets.Length`) só pode fundir no tail se
  `tail.Liquid == liquid`. Se for diferente, sobrescrever o tail e somar o volume mesmo assim —
  conservação de volume continua tendo prioridade sobre fidelidade de identidade, como já está
  documentado no arquivo.
- `DrainAll()` continua devolvendo só o float total; usado quando o componente é desativado, onde
  a identidade não importa.
- `LiquidDefinition` é `ScriptableObject` (tipo referência) dentro de um `struct` em array —
  válido, sem boxing, sem alocação por pacote.

### 7.2 `LiquidPourController`

`Assets/LiquidFX/Runtime/Stream/LiquidPourController.cs`

**Fonte válvula (torneira):** trocar/complementar `valveLiquidColor` por
`[SerializeField] LiquidDefinition valveLiquid;`. Se preenchido, a cor vem dele; senão, cai no
`valveLiquidColor` atual (retrocompatível com a cena `SinkFaucet` como está hoje).

**`MoveLiquid` (hoje linhas 234-256)** — reescrever os passos 1 e 3:

```
1. remover da fonte:
   - modo válvula        → um pacote, liquid = valveLiquid
   - frasco single-layer → um pacote, liquid = null   (comportamento atual)
   - frasco em camadas   → laço RemoveTopML até satisfazer `requested` ou esvaziar,
                           um Enqueue por líquido distinto retornado
                           (guarda de segurança: no máximo MAX_LAYERS iterações)

3. creditar o que chegou:
   - laço `while (flight.TryDequeueArrived(now, out mL, out liquid))`
   - receptor é FlaskVolume em modo camadas E liquid != null
        → receiver.AddLayeredML(mL, liquid)
        → overflow vai para SpillOverflow com liquid.Color
   - caso contrário
        → receiver.AddML(mL, cor)  // cor = liquid?.Color ?? LiquidColor
```

`OnDisable` (109-111): o `DrainAll` continua creditando por `AddML(stranded, LiquidColor)`. Sem
identidade, mas é o caminho de desligamento — aceitável e já documentado.

### 7.3 Quem **não** muda

- `LiquidSurface` (a pia). Não empilha camadas; recebe líquido de camadas como mistura homogênea
  via `AddML(mL, Color)`. É o comportamento desejado para uma pia.
- `ILiquidContainer`. `AddLayeredML` é método só de `FlaskVolume`, com dispatch por type-check no
  controller. Manter a interface enxuta.
- `LiquidStreamRibbon`, `LiquidImpactFX`, `LiquidSpillPuddle`, `LiquidSpillManager`, todos os
  shaders. Já leem cor a cada frame; herdam a cor da camada do topo automaticamente (§6.4).

---

## 8. Ferramentas de editor

### 8.1 Inspector de `FlaskVolume`

`Assets/LiquidFX/Editor/FlaskVolumeEditor.cs` (novo)

- Lista `initialContents` com swatch da cor e nome da categoria em cada linha.
- Barra empilhada, desenhada **na ordem real de densidade**, mostrando o que vai aparecer no
  frasco — o artista percebe na hora que o óleo subiu para cima da água.
- Total em mL vs `capacityML`, em vermelho quando estoura.
- Botões: `Bake Now`, `Esvaziar`.
- Em Play mode: mostrar as camadas vivas (líquido, mL, se está em grupo de mistura).

### 8.2 Inspector de `LiquidCategory`

Avisar em vermelho se outra `LiquidCategory` no projeto usar a mesma `stackDensity` (§3.1).

### 8.3 Validador

`Tools/LiquidFX/Validate Liquid Library` — varre todos os `LiquidCategory` e `LiquidDefinition`
via `AssetDatabase.FindAssets`:

1. **Erro** — duas categorias com `stackDensity` igual (quebra a estratificação).
2. **Erro** — `LiquidDefinition` sem `category`.
3. **Aviso** — duas categorias com densidade a menos de 0.01 de distância (o LVP faz comparação
   exata, então não misturam, mas a ordem fica frágil a edição).
4. **Aviso** — `color.a == 0` (líquido invisível; quase sempre engano, dado que alpha no LVP é
   absorção e não opacidade).

### 8.4 Biblioteca inicial

`Assets/LiquidFX/Generated/Library/` — criar via um item de menu, junto do `LiquidFXBuilder`
existente, para os assets serem reprodutíveis a partir de código como todo o resto do pacote.

Categorias (densidades reais, bem espaçadas):

| Categoria | stackDensity | Nota |
|---|---|---|
| Alcoólico | 0.79 | flutua em tudo |
| Oleoso | 0.88 | flutua sobre água |
| Aquoso | 1.00 | referência |
| Xaroposo | 1.35 | afunda em água |
| Ácido denso | 1.84 | ácido sulfúrico |
| Metálico | 13.5 | mercúrio; fundo absoluto |

Líquidos de exemplo, ao menos dois na mesma categoria para demonstrar a mistura:
`Água` e `Água salgada` (ambos Aquoso — devem se fundir), `Óleo vegetal` (Oleoso),
`Etanol` (Alcoólico), `Xarope` (Xaroposo).

### 8.5 Cena de review

`Assets/LiquidFX/Scenes/LayeredPour.unity`, gerada pelo `LiquidFXBuilder` como as demais:
proveta de origem em modo camadas com 3 camadas (Óleo / Água / Xarope), proveta receptora
começando com água, rig de tilt igual ao das cenas atuais. É onde os critérios de aceite da §9
são verificados a olho.

---

## 9. Ordem de implementação e critérios de aceite

Cada fase tem de compilar e passar seu critério antes da seguinte.

**Fase 1 — Assets e validação.** `LiquidCategory`, `LiquidDefinition`, validador, biblioteca
inicial.
*Aceite:* validador roda limpo sobre a biblioteca gerada; forçar duas categorias com a mesma
densidade produz erro.

**Fase 2 — `FlaskVolume` em modo camadas, só leitura.** Detecção de modo, guardas de §6.3,
`BakeInitialContents`, `TopLiquid`, `LiquidColor`.
*Aceite:* frasco com 3 camadas renderiza na ordem certa de densidade; **console sem nenhum
`LogWarning` de `level`** (é o sintoma de a guarda de `PushLevelToShader` ter faltado); as 3 cenas
atuais seguem idênticas.

**Fase 3 — Saída.** `RemoveTopML`, `RemoveML` em camadas.
*Aceite:* inclinar o frasco esvazia de cima para baixo; a cor do jato **muda** na transição entre
camadas; volume conservado (somar mL retirados = queda do total).

**Fase 4 — Transporte.** `LiquidFlightQueue` com identidade, `MoveLiquid` reescrito, `valveLiquid`.
*Aceite:* despejar num béquer em modo single ainda funciona; nenhum GC alloc por frame durante o
despejo (verificar no Profiler — é requisito de mobile do pacote).

**Fase 5 — Entrada.** `AddLayeredML` com as 5 regras de §6.6.
*Aceite (o teste que importa):* despejar Óleo num frasco com Água ⇒ **duas** camadas, óleo por
cima. Despejar Água salgada num frasco com Água ⇒ **uma** camada, cor média. Este par de casos é
a feature inteira.

**Fase 6 — Editor e cena de review.** Inspectors, `LayeredPour.unity`.
*Aceite:* revisão visual pelo usuário.

---

## 10. Armadilhas conhecidas

1. **`Volume.level` em modo Multiple** — §2.5. Sintoma: enxurrada de warnings. Guarda em
   `PushLevelToShader`.
2. **Crescer `liquidLayers`** — §2.2. Sintoma: camadas com cor aleatória. Alocar `MAX_LAYERS` uma
   vez.
3. **Densidade compartilhada entre categorias** — §2.4. Sintoma: óleo mistura com água. Validador.
4. **`amount` tratado como volume** — §2.6. Sintoma: camadas densas com espessura errada. Usar
   sempre os helpers de §5.
5. **Ler `mixedColor` em vez de `currentColor`** — §2.9. Sintoma: cor do jato adianta a do frasco.
6. **`OnValidate` mutando assets** — Unity avisa. Usar `EditorApplication.delayCall`.
7. **Quirk do LVP em 3364-3374:** o cálculo de `level` indexa `_liquidLayers[k]` onde `k` é
   contador do laço, não `sortedLayers[k]`. Com a compactação ativa o resultado normalmente
   coincide. **Não corrigir código de terceiro.** Se o nível ficar visivelmente errado com muitas
   camadas, contornar do nosso lado; registrar aqui se acontecer.
8. **Trocar `detail` troca material/shader.** Verificar visualmente na Fase 2. Refração já era
   `false` nos frascos atuais (§2.7), então não há regressão esperada.

---

## 11. Fora de escopo

- Reações químicas / transformação de líquidos ao misturar. Aqui, mistura é só média visual.
- Camadas na pia (`LiquidSurface`). Continua homogênea.
- Camadas na poça do chão. Continua com cor única.
- Temperatura, evaporação, precipitado.

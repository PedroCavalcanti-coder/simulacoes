# Tutorial de uso — LabSpillClean

O **LabSpillClean** é uma demonstração de derramamento de líquidos de laboratório em URP. O líquido que sai de um frasco é simulado como partículas pelo solver PBD em GPU e renderizado como uma superfície contínua por *screen-space fluid* (SSF). O projeto também possui frascos, biblioteca de líquidos, mistura/camadas e aquecimento por bico de Bunsen.

## Cena principal

Abra `Assets/LabSpillClean/Scenes/LabSpillDemo.unity` e pressione Play.

A cena reúne:

| Objeto/cenário | Finalidade |
| --- | --- |
| `Frasco - Agua`, `Frasco - Oleo` e `Frasco - Alcool` | Frascos ativos com os líquidos de referência. |
| `Bunsen Burner` | Zona de aquecimento, chama, bolhas e vapor. |
| `Spill Fluid World` | Mundo único que contém o pool de partículas, solver, captura no gargalo e diagnóstico. |
| `Lab Bench Top` e `Lab Ground` | Superfícies com colisores que recebem o líquido. |
| Cenários `Camadas - ...` e `Mistura - ...` | Variações de teste presentes na cena, atualmente desativadas. Podem ser ativadas no Hierarchy para inspeção. |

## Como testar a cena

Esta versão não contém controles de teclado, mouse, toque ou VR. Para provocar um despejo na demo:

1. Entre no Play Mode.
2. Selecione um dos frascos ativos no Hierarchy.
3. Use a ferramenta de rotação do Unity para incliná-lo até que o nível interno alcance o gargalo.
4. O `SpillLiquidContainer` detecta que o líquido alcançou a boca, e o `SpillPourEmitter` cria as partículas do jato.
5. Observe o painel no canto superior esquerdo: ele mostra partículas vivas, taxa de emissão, mortes por idade, capturas, colisores e portas de entrada.

O frasco despeja automaticamente quando está inclinado; não é necessário chamar um método para iniciar o fluxo. A vazão vem do campo **Pour Rate ML Per Sec** e da altura de líquido acima do bico.

Para verificar aquecimento, mova um frasco com líquido sobre a área de trigger `Heating Zone`, filha do Bunsen. O componente `SpillBurner` aquece o conteúdo até a temperatura máxima configurada; ao passar do ponto de ebulição, surgem bolhas e vapor. Desative **Lit** no inspector do Bunsen ou chame `SetLit(false)` para apagar a chama.

## Requisitos técnicos

- Unity com **Universal Render Pipeline (URP)**.
- Hardware e API gráfica com suporte a **Compute Shaders** e leitura assíncrona de GPU. O solver PBD, o grid hash e a ordenação usam `Resources/*.compute`.
- O Renderer Data em uso deve conter a feature **Spill Fluid (Single Settings)** (`SpillFluidRenderFeature`). O arquivo `Assets/LabSpillClean/Settings/PC_Renderer.asset` já a inclui e a conecta a `SpillVisualSettings`.
- A câmera precisa gerar textura de profundidade; a feature solicita a profundidade durante o passe de renderização.

Se você usar outro URP Renderer Data, adicione **SpillFluidRenderFeature** pela lista *Renderer Features* e atribua o mesmo asset de configurações visual usado pelo `SpillFluidWorld`.

## O que cada parte faz

```text
SpillLiquidContainer
  └─ detecta o despejo conforme o nível e a inclinação do frasco
       └─ SpillPourEmitter
            └─ SpillFluidWorld
                 ├─ FluidPool + FluidSolver (PBD em GPU)
                 ├─ captura partículas que entram no gargalo de outro frasco
                 ├─ colisores da cena e descarte por tempo de vida
                 └─ SpillFluidRenderFeature (SSF: profundidade → blur → normal → superfície)

SpillBurner
  └─ aquece o líquido dentro da Heating Zone e cria bolhas/vapor
```

| Componente | Responsabilidade |
| --- | --- |
| `SpillFluidWorld` | Gerencia o pool fixo de partículas, o solver, todas as substâncias, colisores, portas de captura e a publicação do render. Deve existir apenas uma vez por cena. |
| `SpillVisualSettings` | Fonte central dos parâmetros de volume por partícula, orçamento, SSF, PBD, emissão, vida e colisão. |
| `SpillLiquidContainer` | Componente atualmente usado pela cena para volume interno, nível, mistura, ondas, temperatura e decisão de derramar. |
| `SpillPourEmitter` | Converte mL aceitos pelo mundo em partículas de volume fixo e as emite no gargalo. |
| `SpillFluidRenderFeature` | Passes URP para reconstruir a superfície líquida das partículas. |
| `SpillBurner` | Detecta recipientes na zona de calor, altera a temperatura e instancia os efeitos de fervura. |
| `SpillColliderProvider` | Envia automaticamente os colisores do domínio para a GPU. |
| `SpillColliderExclude` | Marcador opcional para ignorar um objeto e seus filhos na colisão do fluido. |
| `SpillFluidDebugHUD` | Painel de diagnóstico em Play Mode; pode ser ocultado com `show = false`. |

## Ajuste central do fluido

Edite `Assets/LabSpillClean/Settings/SpillVisualSettings.asset`. Esse é o lugar correto para ajustar o comportamento global do derramamento.

| Grupo | Campos principais | Efeito |
| --- | --- | --- |
| Volume | `millilitersPerParticle`, `maxParticles` | Define o volume exato de cada partícula e o teto total do pool. Não exceda o limite do solver ao aumentar o orçamento. |
| Tamanho visual | `visualRadiusScale` | Aumenta apenas o splat do SSF; não muda o volume nem a colisão física. |
| Superfície SSF | `resolutionScale`, `blurRadius`, `blurIterations`, `normalRadius`, `depthFalloff`, `surfaceTension`, `edgeSoftness`, `densityThreshold` | Controlam resolução, suavização e leitura visual da superfície. |
| PBD | `solverIterations`, `constraintIterations`, `maxPhysicsStepsPerFrame`, `viscosity`, `massScale`, `restDamping`, `cohesion` | Equilibram estabilidade, viscosidade e custo da simulação. |
| Emissão e vida | `maxParticlesPerFrame`, `particleLifetimeMin`, `particleLifetimeMax` | Limitam a emissão por frame e definem por quanto tempo uma partícula continua viva. |
| Colisão | `colliderRefreshInterval`, `maxColliders` | Definem a frequência de varredura e o número máximo de colisores enviados à GPU. |

Uma partícula representa sempre o mesmo volume. Com o perfil padrão, cada uma equivale a **1 mL**; o raio físico é calculado automaticamente assumindo que 1 unidade Unity = 1 metro. Não altere o raio físico por um script separado: use `millilitersPerParticle` para coerência de volume.

## Como criar uma cena nova

1. Crie ou copie uma cena URP e configure a câmera/luz.
2. No Renderer Data usado pela cena, adicione **SpillFluidRenderFeature** e atribua `SpillVisualSettings.asset`.
3. Crie um GameObject `Spill Fluid World` e adicione **SpillFluidWorld**.
   - Atribua o mesmo `SpillVisualSettings.asset`.
   - Atribua `Materials/PBDFluid_SSF_Liquid.mat` em **Surface Material Template**.
   - Mantenha **Auto Fit Domain** ativo para o mundo enquadrar os colisores da cena, ou desative-o e informe **Simulation Bounds** explicitamente.
4. Modele a bancada/chão com `BoxCollider`, `SphereCollider` ou `CapsuleCollider`. O mundo encontra automaticamente os colisores dentro do domínio.
5. Para que o líquido atravesse um objeto, adicione **SpillColliderExclude** nele. Recipientes com `SpillLiquidContainer` já são excluídos automaticamente, evitando que a caixa do frasco bloqueie sua própria boca.
6. Crie um frasco e adicione:
   - `MeshFilter` e `MeshRenderer` para o líquido interno;
   - `SpillLiquidContainer` no objeto do líquido;
   - `SpillPourEmitter` no frasco ou em um pai, apontando para o recipiente, o `SpillFluidWorld` e o material SSF;
   - um `Collider` no objeto externo do frasco, caso queira manipulá-lo/identificá-lo. Esse colisor não será enviado à simulação por causa da exclusão automática do recipiente.
7. Incline o frasco por animação, Rigidbody ou seu script de interação. Ao alcançar o bico, o sistema despeja.

Para receber o líquido em outro frasco, use outro `SpillLiquidContainer` com abertura válida. O mundo verifica a trajetória entre amostras, por isso uma partícula rápida que cruzar o plano circular do gargalo ainda é capturada e convertida no volume correspondente.

## Configuração de um frasco atual

No `SpillLiquidContainer`, configure:

- **Cavity Mesh**: malha interna que representa a cavidade do frasco. Se estiver vazia, usa a malha do próprio objeto.
- **Voxel Resolution**: resolução para o bake da cavidade; valores maiores melhoram a precisão, mas aumentam custo/memória.
- **Capacity ML** e **Current Volume ML**: capacidade e conteúdo atual em mililitros.
- **Liquid Config File**: JSON de aparência e propriedades do líquido. Se vazio, usa `Resources/DefaultLiquidConfig.json`.
- **Initial Composition**: mistura/camadas iniciais do sistema atualmente ativo na cena.
- **Pour Rate ML Per Sec**: vazão máxima enquanto houver líquido acima do gargalo.
- **Graduation Calibration**: pares volume/altura para alinhar o nível visual às marcações reais do frasco.
- **Slosh / Waves**: resposta visual a aceleração, giro e movimento vertical.

Os JSONs iniciais ficam em `Assets/LabSpillClean/Configs/`: água, óleo, álcool e o perfil padrão. Eles determinam cor, densidade, viscosidade, propriedades ópticas e efeitos térmicos da implementação que a cena usa hoje.

## Biblioteca de líquidos nova

O projeto também contém a nova biblioteca baseada em `ScriptableObject`:

- `Liquids/Categories/`: categorias de mistura.
- `Liquids/Liquids/`: água, óleo, álcool e líquido padrão.
- `SpillLiquidDefinition`: identidade, densidade, aparência, comportamento térmico e cor do jato.
- `SpillFlaskVolume`: ponte entre mililitros e camadas do LiquidVolumePro.

Use os menus abaixo para manter essa biblioteca:

- **Tools > Lab Spill > Converter JSONs em assets de liquido**: converte os JSONs em categorias e assets reutilizáveis.
- **Tools > Lab Spill > Validar biblioteca de liquidos**: procura referências ausentes e ambiguidades de densidade/categoria.
- **Assets > Create > Lab Spill > Liquid Category** e **Assets > Create > Lab Spill > Liquid**: cria assets manualmente.

Regras da biblioteca nova:

- A **categoria** decide se os líquidos se misturam.
- A **densidade** do líquido decide a ordem de empilhamento: o menos denso flutua.
- Líquidos da mesma categoria são combinados em uma camada visual; categorias diferentes permanecem em camadas separadas.
- O alfa da cor representa absorção volumétrica, não opacidade comum.

> **Estado atual:** a demo `LabSpillDemo.unity` ainda usa `SpillLiquidContainer` e `LiquidConfig`/JSONs. `SpillFlaskVolume` e `SpillLiquidDefinition` já existem no projeto, mas são parte de uma migração descrita em `PLANO-REFORMA.md` e não substituem o caminho ativo da cena ainda. Não misture os dois fluxos no mesmo frasco sem concluir essa integração.

## Aquecimento e ebulição

No `SpillBurner`, ajuste:

- **Lit**: liga/desliga a chama.
- **Heating Zone**: `BoxCollider` trigger que define a área aquecida.
- **Heating Rate C Per Second** e **Cooling Rate C Per Second**: taxas de aquecimento e resfriamento.
- **Maximum Temperature C** e **Ambient Temperature C**: limites térmicos.

O líquido só ferve quando sua temperatura chega ao ponto de ebulição configurado. As partículas de bolha ficam presas no líquido e o vapor nasce acima do frasco. O sistema cria esses efeitos durante o Play Mode; eles não são objetos persistentes da cena.

## Diagnóstico e solução de problemas

| Sintoma | Verificação / solução |
| --- | --- |
| Não aparece líquido em queda | Confirme que há um `SpillFluidWorld` ativo, o emissor referencia o mundo e o frasco foi inclinado até o nível alcançar o gargalo. Confira também `Current Volume ML`. |
| O líquido não é renderizado | No Renderer Data, confirme que **Spill Fluid (Single Settings)** está ativo e aponta para o mesmo `SpillVisualSettings` do mundo. |
| A cena não inicia ou mostra erro de Compute Shader | Teste em uma plataforma com suporte a Compute Shaders e confira os arquivos em `Resources/`. |
| O fluxo para antes de acabar o frasco | Aumente `maxParticles`, reduza a vida das partículas, reduza o volume inicial ou verifique o HUD para saber se o pool está cheio. |
| Líquido atravessa a bancada/chão | Confirme que a superfície tem collider, está dentro de `Simulation Bounds` e não possui `SpillColliderExclude`. |
| O líquido bate no próprio frasco e não entra no destino | O recipiente deve ter `SpillLiquidContainer`; isso exclui automaticamente o collider da vidraria. Verifique a posição e a abertura do receptor. |
| Jato parece grosso/fino, mas o volume está correto | Ajuste `visualRadiusScale`; ele não altera a física. Para alterar a escala física, ajuste `millilitersPerParticle`. |
| Poça ou jato está instável | Aumente com moderação `solverIterations`/`constraintIterations` ou a coesão; isso eleva o custo de GPU. |
| Não há vapor/bolhas | Deixe o frasco com conteúdo dentro da `Heating Zone`, ligue o Bunsen e confirme que a temperatura máxima alcança o ponto de ebulição do líquido. |

## Estrutura de arquivos

```text
Assets/LabSpillClean/
├── Scenes/LabSpillDemo.unity       cena de demonstração
├── Settings/SpillVisualSettings.asset
├── Settings/PC_Renderer.asset      Renderer Data com a feature SSF
├── Scripts/Core/                   pool, solver, hash espacial e ordenação
├── Scripts/Runtime/                mundo do fluido, queimador, HUD e colisores
├── Scripts/Rendering/              Renderer Feature e ponte de renderização
├── Scripts/Liquid/                 recipientes, emissão e nova biblioteca de líquidos
├── Resources/                      compute shaders
├── Shaders/                        shaders da superfície, blur, profundidade e partículas
├── Materials/                      materiais de líquido, vidro, chama e ambiente
├── Configs/                        JSONs usados pela demo atual
├── Liquids/                        ScriptableObjects da nova biblioteca
├── Editor/                         conversor, validação e inspector de frasco
└── Screenshots/                    registro visual dos testes e calibrações
```

## Checklist antes de usar em outra cena

- [ ] O Renderer Data URP contém `SpillFluidRenderFeature`.
- [ ] `SpillFluidWorld` tem `SpillVisualSettings` e material SSF atribuídos.
- [ ] O domínio da simulação inclui todas as superfícies relevantes.
- [ ] Bancada/chão possuem colisores simples e não estão marcados para exclusão.
- [ ] Cada frasco emissor tem `SpillLiquidContainer` e `SpillPourEmitter` configurados.
- [ ] O perfil de volume por partícula e o limite de partículas foram testados na GPU-alvo.
- [ ] A biblioteca de líquidos foi validada se você usa `SpillLiquidDefinition`.
- [ ] Você escolheu um único fluxo por frasco: o atual (`JSON + SpillLiquidContainer`) ou a migração (`ScriptableObjects + SpillFlaskVolume`).

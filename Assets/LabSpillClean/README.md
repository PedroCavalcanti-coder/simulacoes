# Lab Spill Clean

Cena principal: `Scenes/LabSpillDemo.unity`.

## Um unico lugar para ajustar o fluido

Edite `Settings/SpillVisualSettings.asset`. Ele controla os valores que antes
apareciam duplicados em varios componentes:

- `millilitersPerParticle`: volume exato de cada particula (1 mL no perfil portatil);
- `PhysicalRadius`: calculado automaticamente pelo volume, assumindo 1 unidade Unity = 1 metro;
- `visualRadiusScale`: ajuste apenas visual do SSF, sem alterar volume/colisao;
- `resolutionScale`, `blurRadius`, `blurIterations`, `normalRadius`,
  `depthFalloff` e `surfaceTension`: reconstrucao/embaçamento;
- iteracoes, viscosidade, massa e amortecimento: custo e comportamento PBD;
- `benchLifetimeMin/Max`: remocao aleatoria na bancada.

`SpillFluidRenderFeature` apenas executa profundidade, blur e normal. Ela le o
asset acima e nao possui uma segunda copia desses valores.

## Componentes da cena

- `SpillPourEmitter`: fica no frasco de origem e emite pelo disco do gargalo;
- `SpillLiquidContainer`: volume e shader dentro de cada frasco;
- `SpillFluidWorld`: solver compartilhado, multiplos liquidos, recepcao e vida;
- `SpillSurface`: marca bancada ou chao;
- `SpillBurner`: aquece o liquido dentro da zona, resfria fora dela e cria
  bolhas/vapor usando cor, tamanho e taxas do mesmo JSON do liquido.

O frasco receptor verifica a trajetoria entre amostras, portanto uma particula
rapida que atravesse o plano circular do gargalo tambem adiciona exatamente o
volume configurado por particula. O raio
de captura inclui a escala visual do SSF, evitando a situacao em que a gota
parece entrar mas seu centro fisico passa poucos milimetros fora.

## Dependencias

A cena, o PC Render Pipeline e a Renderer Data foram auditados e nao possuem
dependencias de assets fora de `Assets/LabSpillClean` (pacotes Unity/ProBuilder
continuam sendo dependencias do projeto). O arquivo
`Assets/PBDFluid/LegacySolverCompatibility.cs` existe apenas para as demos
antigas ainda compilarem enquanto essa pasta continuar no projeto; ele nao e
usado pela cena limpa e pode ser apagado junto com `Assets/PBDFluid`.

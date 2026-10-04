# Autonomous roadmap prompt

The standing brief for completing the port's goal sequence autonomously, including every
pending item known when it was written (2026-10-04). Pass it to a fresh session, for
example with `/goal implement docs/AUTONOMOUS_ROADMAP_PROMPT.md`.

````text
Você vai concluir, de forma autônoma e até o fim, o roteiro de objetivos do port .NET de
Dark Colony (1997), inclusive todas as pendências conhecidas listadas abaixo. Responda ao
usuário sempre em português. Não pare para pedir confirmação. Pergunte ao usuário só o que
nenhuma evidência estática ou de execução consegue responder, como a "sensação" do jogo
original.

## Contexto
- Repo: C:\Users\LY\Desktop\darkcolony\dc-port-dotnet. O remote é
  git@github.com:jonathands/mars-dark-colony-remake.git (PÚBLICO).
- Branch de trabalho: stabilize/gameplay-baseline. O master está em 1c9e3a1; não abra PR
  sem pedido.
- Instalação original: C:\Users\LY\Desktop\darkcolony\Dark Colony (dc.exe e dados).
  - O executável autoritativo é dc.exe, não dc16.exe.
  - Rode o jogo original SÓ via `Dark Colony/launch.ps1`, porque há risco de truncar o
    anim.dat.
- Leia antes de começar:
  - AGENTS.md, README.md, docs/PORT_CONTRACT.md, docs/DETERMINISM.md,
    docs/RECONSTRUCTION_CONTEXT.md;
  - docs/reverse-engineering/*.md, principalmente city-and-economy.md,
    production-flow.md, target-acquisition.md e mission-triggers.md, com suas tabelas de
    status;
  - a memória em C:\Users\LY\.claude\projects\C--Users-LY-Desktop-darkcolony\memory\
    (goal-sequence.md, dev-environment-quirks.md);
  - `git log --oneline -30`.
- Autorizações já dadas: instalar ferramentas via winget, commitar e dar push no GitHub,
  montar a ISO do CD do usuário, rodar o original para capturas e leitura de memória.

## Ferramentas
- dotnet no Bash: "/c/Program Files/dotnet/dotnet.exe".
- Build: `dotnet build DarkColony.Port.sln`. Feche o DarkColony.App antes, porque ele
  trava as DLLs.
- Checks: `dotnet run --project tests/DarkColony.Engine.Checks` (uns 45 s).
  - Modos: `-- --verify-goldens`, `--update-goldens`,
    `--dump-digest <cenário> <tick> <arquivo>`, `--event-summary <cenário> <ticks>`,
    `--coverage-scan <ticks>`.
  - `DARKCOLONY_CHECK_FILTER=<texto>` roda só os checks cujo nome contém o texto.
- Ghidra headless (no Bash):
  `cmd //c ".tools\\decompile.cmd C:\\Users\\LY\\AppData\\Local\\Temp\\out.txt 0xADDR refs:0xADDR ..."`
  O comando decompila a função que contém cada endereço e lista os chamadores; `refs:`
  lista as referências. O Ghidra perde registradores em parâmetros (EBX/AL); nesses
  casos, confirme no disassembly.
- Disassembly (capstone), a partir de C:\Users\LY\Desktop\darkcolony:
  - `PYTHONPATH=dc-port-26/.tools/pydeps python dc-port-26/tools/disasm_range.py "Dark Colony/dc.exe" 0xINI 0xFIM`
  - `python dc-port-26/tools/disasm_displacements.py "Dark Colony/dc.exe" 0xDISP --context 0`
- Mapeamento: arquivo 0x6FC00 ↔ VA 0x472000 (DGROUP). As strings `*.c` dão o mapa de
  módulos.
- Tabela de tratadores de comando de ator: `0x4792B8 + 4·(20+tipo)`. `0x411DD8` empilha
  comandos (tamanho em ECX, tipo em EBX).
- App visual: na ferramenta PowerShell, chame o script direto (NÃO via `pwsh -File`):
  `& ./tools/Run-Port.ps1 -ExtraArguments '--single-player-war' -Actions 'wait:2500','click:574,463','wait:4000' -Seconds 6 -Name nome`
  - Pontos de clique: READY do War (574,463); NEW CAMPAIGN (229,325); START CAMPAIGN
    (489,361); NEXT da história (588,461); `key:Left` etc. rola a câmera.
  - Capturas ficam em screenshots/ (abra e confira); log em artifacts/logs/<nome>.log.
  - Capturas do original ficam em `Dark Colony/screenshots/`.
- Armadilhas:
  - Heredocs do Bash colapsam `\\` em `\`. Para edições com barras invertidas, use a
    ferramenta Edit ou um script Python gravado com a ferramenta Write.
  - O worktree usa CRLF. Preserve o fim de linha ao editar por script.

## Regras inegociáveis
- Nunca copie assets nem tabelas de dados do jogo original para o repo.
  - Leia tabelas do dc.exe em tempo de execução via PeImage.
  - Só constantes curtas de algoritmo podem ficar no código, sempre com o endereço
    documentado.
- Toda regra nova vem de evidência do exe (endereço em comentário e em doc) ou de captura
  do original. Na documentação, cada regra fica marcada como confirmada, provisória ou não
  modelada.
- Determinismo:
  - os goldens só mudam por mudança intencional, explicada no commit;
  - toda propriedade pública da ScenarioSimulation entra no digest;
  - nada de float nem de relógio de parede na simulação.
- Cada mudança de comportamento ganha um check focado (InternalsVisibleTo já está liberado
  para os checks).
- Commits coerentes e pequenos, com push ao fim de cada entrega. A mensagem termina com:
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: <link da sessão atual>
- Mantenha atualizados: docs/reverse-engineering/*.md (com tabela de status),
  docs/PORT_CONTRACT.md (partials e pastas novas) e goal-sequence.md na memória.
- Seja honesto: diga o que não foi verificado, o que falhou e o que ficou provisório.
  Nunca invente resultado.

## Ciclo de trabalho para cada item
1. Trace no dc.exe.
2. Implemente no partial certo de ScenarioSimulation, ou em Engine/<Pasta>.
3. Escreva checks: unitários sintéticos e com o jogo instalado.
4. Rode a suíte completa. Se os goldens mudaram, explique, regenere e mantenha a
   explicação.
5. Confira visualmente no app quando houver efeito visível.
6. Documente, commite, dê push e atualize a memória.
7. Mande ao usuário um resumo curto.

## Estado ao escrever (concluído)
Objetivos 0 a 2. Do objetivo 3:
- cidades nativas, com a origem na 2ª linha do `%AISlots` e o pedestal no bit 9 do
  atributo do MAP;
- renda passiva de +3 a cada 16 ticks com filtro pela sede, e renda do respiradouro com o
  mesmo filtro;
- relação cooperativa entre jogadores e o time 9;
- aquisição automática de alvo;
- ceder passagem (+0x35);
- filas de produção com saída `0x41ADD0` e tempo da animação BUILD;
- construção por slot (comando 9);
- pré-requisitos ligados ao slot vivo;
- passo bloqueado nativo (`0x415458`), com rota para célula ocupada.

Do objetivo 4: o motor `.tro`/`.mtg` com compilador e VM fiéis, executores `norm`/`trip`,
estatísticas e a maioria das ações.

Do objetivo 5: debriefing e avanço de campanha no app.

## Roteiro restante, em ordem

### A. Fechar TODAS as pendências dos objetivos 3 e 4
Cada item é implementado fielmente ou justificado por escrito na tabela de status.

**A1. Economia, cidade e produção**
- Limite de tropas `world+0x528` (`0x41E6AC`; contagens em `0x4956E0` via `0x41A538`), com
  reembolso do P7 e aviso 0x77 (`0x414314`).
- Prólogo de animação de dano em `0x414314` (flag +0x1A do ator), que pode interromper a
  animação BUILD.
- Ao destruir um prédio de cidade, o nativo deixa a marca de reserva 0x3FE na saída; o
  port a libera. Decida e documente.
- Gating de sessão multiplayer: times fora da sessão têm a vida dos slots zerada
  (`local_18+0x14A0`, `+0x1524+time`, no loader `0x41B920`).
- No loop de posicionamento do SCN:
  - troca de entidade por raça (`DAT_004F1994`, contraparte quando a raça do time difere);
  - desvio de posicionamentos para listas de coleta (`0x4404C0`);
  - índices 120–151 reservados.
- Papel da 1ª linha do `%AISlots` (player +0xBCC/+0xBD0); o comandante nasce ali.
- Fase dos pulsos:
  - renda passiva usa world+0x94C; respiradouro usa world+0x530; hoje ambos usam
    `simulationTicks`;
  - `c` usa world+0x52C;
  - confirme o mapeamento de cada contador e a ordem dentro de `0x4196F4`.
- Renda do respiradouro:
  - quantidade = palavra alta de +0x30 da fonte, multiplicador +0x19B8 do jogador com a
    flag +0xBBC;
  - mapear os campos do SCN de respiradouro (o `InitialState` do port);
  - trocar o `AttachedP7PerPulse` global provisório pela taxa por respiradouro.
- Ladrão de P7 (SARGSTL/PSYCSTL): o alcance de 12 células Chebyshev é provisório; trace a
  distância nativa.
- Time sem cidade: decida o destino dos adaptadores do port (produção instantânea e
  `PlaceBuildingIntent` livre) com base no nativo.

**A2. Movimento e combate**
- Bit de "revelado" das minas hostis (a aquisição hoje as ignora).
- Comando "fidget" (tipo 1, tratador `0x419238`) e o efeito visível.
- `PackedPathPlayback.Cancel`: recuperar a interpolação de parada nativa.
- Depois da espera de 4 execuções no passo bloqueado, o nativo repete os passos guardados;
  o port refaz a rota. Alinhe.
- O fallback embaralhado do ceder passagem aceita ator "estado 1" (`+0x2C == 1`); o port
  usa "não destruído". Refine.
- Movimentos empilhados pelo comando ocioso (aproximação, ceder passagem) andam no mesmo
  tick no port. Verifique se no nativo começam no tick seguinte.
- Estatística 11 (abates a até 20 células do comandante) e estatísticas 5, 8 e 9:
  modelar.

**A3. Scripts de missão (objetivo 4)**
- **`reinforce` com nave real.**
  - Nave: DROP (92) para humanos, SAUC (93) para alienígenas, time 8.
  - Rotina de entrada: `0x418F4C`.
  - Comandos 0x15 (entrega, tratador `0x4187E4`) e 0x16 (voo, `0x4182E8`/`0x4186E0`);
    `0x418504` cria o ator auxiliar.
  - Voo, pouso, desembarque e partida com tempos nativos.
- **`abduct`** (caso 0x13 de `0x43D814`).
- **`artifact`**: listas de coleta em `0x4404C0`; entidades 0x3F–0x43.
- **`vision` e bits de aliança do `ally`**:
  - `0x41E7D8` grava os bits; `0x41E820` os lê;
  - world+0x471A0/0x471A4;
  - `0x4196F4` recalcula as linhas 0–7 da matriz a cada atualização;
  - o painel de Aliados do app também tem de usar os bits.
- **`nopickup`** (+0xBB4) e **`noundeploy`** (world+0x948): os consumidores desses flags.
- **`waypoint`**: estado 9 (`0x43D764`, +0x36/+0x37/+0xC6); patrulha ou passada única?
- **Multiplicadores de `newrate`/`newrate2`/`setmoney`** (`0x41A538` com k=1 e k=2).
- **`newtype`**: hoje usa o override de forma implantada; troque por uma troca real de
  tipo, mantendo vida e identidade.
- **`u(i)`**: quem escreve `0x4FE04C`?
- **Piso de pilha** das condições malformadas de human09/alien08: descubra o que o nativo
  lê abaixo da pilha.
- **Atraso do `bail`**: são 10.000 ms de relógio real; hoje é aproximado por 152 ticks.
- **Opções do lobby** no `stat 0` (`0x401848`–`0x4018A2`, globais
  `0x494680`–`0x494694`). Os scripts multiplayer de renascimento de respiradouro dependem
  de `s(3,0)`/`s(4,0)`.
- **`aimsg`**: deixe pronto para alimentar a IA.
- **Ordem do executor `norm`** dentro do tick em relação à renda e aos atores: confirme.

Pronto quando:
- os 22 tipos de ação estão modelados;
- a tabela de status de mission-triggers.md não tem item "não modelado" sem justificativa;
- checks headless levam human01 e alien01 do início à vitória.

### B. Objetivo 6: IA do computador (Krusty)
- Módulos:
  - `krusty.c`: `0x44BA48`;
  - `krusty_general.c`: `0x4566AC`–`0x4571AC` (inclui as compras);
  - `krusty_attack.c`: `0x457A60`–`0x458814`;
  - `krusty_defend.c`: em torno de `0x4591A0`;
  - `krusty_army.c`: `0x463840`–`0x464100`;
  - `krusty_scout.c`;
  - `ai.c`: `0x41AA70`/`0x41AB20`.
- A inicialização (`0x44BD2C`) aloca 0x6C40 bytes de estado por jogador (ponteiro em
  player+0xBC0).
- A IA ativa com player+0xBBC ≠ 0.
- Ordem de porte:
  1. Estado e cadência.
  2. Economia, coleta e compras pelos comandos 9 e 10 já portados.
  3. Defesa.
  4. Exército e ataque.
  5. Exploração.
  6. Integração com `aimsg`.

Pronto quando:
- num War local, o computador colhe, constrói, produz e ataca sozinho (check headless com
  metas mensuráveis e conferência visual);
- as bases inimigas de campanha agem.

### C. Objetivo 5: campanha completa
Pronto quando:
- as 15 missões humanas, as 15 alienígenas e os 14 treinos carregam e rodam o script
  (confira também o human16 e outros extras do diretório);
- briefing → jogo → debriefing → próxima missão funciona no app;
- um smoke headless de campanha prova que cada missão alcança um desfecho, ou documenta o
  motivo;
- salvar e carregar existe se viável: a referência nativa é o loader `0x41A978`, e o
  estado restaurado deve dar o mesmo digest.

### D. Objetivo 7: polimento e pendências visuais
Itens:
- âncora do sprite dos prédios: a sede aparece cerca de 2 tiles abaixo e à esquerda das
  capturas do original; a causa está na âncora do quadro, não no Y da camada; trace o
  blit da fila de sprites de `0x435FCC`;
- tempo de animação no app: atraso nativo `((d==0?15:d)+3)*15/100` ticks por quadro
  (passo `0x4264C8`), no lugar do `/3`; carregue os FINs na ordem do anim.dat;
- renomeie `LogicalFrame.Event` para atraso de quadro;
- paleta dos botões do menu principal: hoje borda verde e texto ciano, no original
  contorno e texto vermelhos;
- créditos rolantes que faltam no menu;
- prévia preta do trooper na enciclopédia;
- `NativeBearing` sem `Math.Cos`/`Sin`/`Atan`;
- música do CD (DCUK.bin, 4 faixas de áudio);
- vídeos Cinepak (`avi.c`);
- memória de terreno explorado (névoa);
- tela de opções.

Pronto quando: cada item está fechado e verificado por captura, ou documentado como não
viável.

### E. Objetivo 8: rede e replay
- Replay: grave o fluxo de comandos por tick e reproduza com digest idêntico.
- Lockstep em LAN entre duas instâncias, sincronizado pelo digest, com detecção de
  dessincronia.
- O original usa DirectPlay (`dplay.c`, `net.c`, `sync.c`); trace só o necessário.

Pronto quando: um check grava e reproduz uma partida bit a bit, e duas instâncias locais em
lockstep ficam sincronizadas por N ticks.

## Encerramento
Ao terminar cada bloco (A1, A2, A3, B, C, D, E), atualize goal-sequence.md e mande ao
usuário um resumo em português (o que entrou, os commits, o que ficou provisório). Siga
para o próximo sem esperar resposta. Se algo bloquear de verdade, documente o bloqueio,
siga para o próximo item e volte a ele depois. O trabalho termina quando todos os blocos
atingirem o critério de pronto, ou quando cada exceção restante estiver justificada por
escrito.
````

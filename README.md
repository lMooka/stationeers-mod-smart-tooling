# Smart Tooling

Mod de Stationeers (BepInEx + StationeersLaunchPad). Olhe para uma estrutura, aperte uma tecla e o mod
leva para as suas mãos os itens e ferramentas que ela exige.

## Modos

| Modo | O que vai para as mãos |
|------|------------------------|
| **Construction** | Mão ativa: item principal do próximo estágio (ex.: maçarico). Outra mão: o material (ex.: chapas de aço). Se a estrutura estiver danificada, os itens de conserto; se estiver completa e tiver upgrade, os itens do upgrade. |
| **Deconstruction** | Mão ativa: a ferramenta que desmonta o estágio atual (ex.: esmerilhadeira, chave inglesa, pé de cabra). |

## Teclas (padrão)

| Tecla | Ação |
|-------|------|
| `X` | Seleciona os itens para a estrutura apontada, conforme o modo atual |
| `Shift+X` | Alterna entre Construction e Deconstruction |
| `Ctrl+X` | Devolve os itens para onde estavam |

As teclas são configuráveis em `BepInEx\config\mooka.stationeers.smarttooling.cfg` (o arquivo é criado na
primeira vez que o jogo abre com o mod), seção `[Hotkeys]`. Formato: tecla principal e modificadores
separados por `+`, por exemplo `Select = F`, `ToggleMode = F + LeftAlt`. Os nomes são os de
`UnityEngine.KeyCode`. Quando dois atalhos usam a mesma tecla, vale o que tem mais modificadores.

Outras opções (seção `[Display]`): `ShowMessages`, `ShowModeIndicator` (mostra o modo o tempo todo),
`MessageSeconds`, `PlaySounds`.

## Comportamento

- Os itens são procurados nas mãos, na mochila, no cinto de ferramentas e no uniforme (inclusive na
  mochila presa ao traje). Os slots internos do traje (cilindros, bateria, filtros) nunca são tocados.
- O que estiver na mão é guardado antes: volta para o slot de origem ou, se não der, para um slot livre
  (slots dedicados, como os do cinto, têm prioridade). Sem slot livre, tenta uma troca direta.
- Para empilháveis, prefere a pilha que tem a quantidade exigida; para ferramentas, a que está operável.
- Se faltar um item, o mod avisa na tela qual é e ainda coloca nas mãos o que encontrou.
- O mod só escolhe os itens. Ligar a ferramenta (ex.: acender o maçarico) e clicar continua com você.
- Funciona só do lado do cliente; usa as mesmas mensagens de inventário da interface do jogo.

## Compilar e instalar

```powershell
powershell -ExecutionPolicy Bypass -File SmartTooling\deploy.ps1
```

Instala em `Documentos\My Games\Stationeers\mods\SmartTooling`.

## Autoteste

```powershell
powershell -ExecutionPolicy Bypass -File SmartTooling\selftest.ps1
```

Exige o jogo fechado. Abre o Stationeers em um mundo descartável, cria uma armação de aço, executa
seleção / alternância de modo / devolução, confere as mãos e constrói um estágio de verdade com os itens
escolhidos. O resultado sai no console (`SELFTEST PASS/FAIL`) e a versão normal é reinstalada no final.
O autoteste não cobre o teclado em si, o texto na tela nem o papel de cliente em multiplayer.

## Arquivos

- `Plugin.cs` — configuração, teclas, mensagens na tela.
- `Resolver.cs` — o que cada estrutura exige (espelha `Structure.AttackWith`).
- `Equipper.cs` — busca no inventário, movimentação e devolução dos itens.
- `SelfTest.cs` — autoteste (só entra na DLL com `-p:SelfTest=true`).

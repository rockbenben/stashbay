# StashBay

> Uma prateleira leve para transportar arquivos entre janelas: solte dentro, arraste para fora e pronto — sem precisar alinhar janelas lado a lado.

[简体中文](../../README.md) · [English](../../README.en.md) · [繁體中文](README.zh-Hant.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Español](README.es.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · **Português** · [Русский](README.ru.md) · [Italiano](README.it.md) · [العربية](README.ar.md) · [हिन्दी](README.hi.md) · [বাংলা](README.bn.md) · [ไทย](README.th.md) · [Türkçe](README.tr.md) · [Tiếng Việt](README.vi.md) · [Bahasa Indonesia](README.id.md)

[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](../../LICENSE) [![365 Open Source Plan #048](https://img.shields.io/badge/365%20Open%20Source%20Plan-%23048-1f6feb)](https://github.com/rockbenben/365opensource)

[⬇ Baixar versão mais recente](https://github.com/rockbenben/048-stashbay/releases/latest)

![Prateleira compacta StashBay: um pequeno rack sempre no topo para reter e mover arquivos](../../assets/shelf.png)

Estação leve e local de trânsito de arquivos para a área de trabalho do Windows. Funciona totalmente offline, sem envio para a nuvem, sem solicitações de rede e sem necessidade de criar conta. Atua tanto como uma prateleira transitória prática de usar e fechar quanto como uma bancada completa de arquivos com reconhecimento de Git, backups por instantâneos e desfazimento em um clique.

---

## Especificações de suporte

| Dimensão | Especificações |
| :--- | :--- |
| **Sistema operacional** | Windows 10 (1809+) / Windows 11 (suporte nativo para x64 e ARM64) |
| **Dependências** | As versões instalador e portátil oficiais trazem o runtime integrado; compilações enxutas requerem o .NET 10 Desktop Runtime |
| **Itens aceitos** | Arquivos de qualquer formato e pastas multinível (preserva a hierarquia relativa de diretórios) |
| **Fontes de entrada** | Seleção no Explorador de Arquivos, lista de arquivos da área de transferência, gesto de sacudir o mouse |
| **Destinos de gravação** | Discos locais, compartilhamentos de rede (UNC / unidades mapeadas), repositórios Git |
| **Arquivos grandes** | Suporta arquivos individuais > 2 GB e caminhos longos do Windows (> 260 caracteres) |
| **Permissões** | Executado por padrão com privilégios de usuário padrão (instalado em `%LOCALAPPDATA%\Programs`); inicialização opcional como administrador |
| **Armazenamento** | Salvo por padrão em `%LOCALAPPDATA%\StashBay\` (totalmente compatível com a pasta herdada `FileStash`) |

---

## Início rápido

1. **Obter o programa**: Baixe o instalador `StashBay-vx.y.z-setup.exe` (não requer administrador) ou o pacote portátil `StashBay-vx.y.z-win-x64.zip` em [Releases](https://github.com/rockbenben/048-stashbay/releases/latest).
2. **Abrir a prateleira**: Pressione o atalho <kbd>Win+Alt+S</kbd>; a mini-prateleira se abrirá instantaneamente ao lado do ponteiro do mouse na tela ativa.
3. **Guardar arquivos**: Selecione os arquivos no Explorador e pressione <kbd>Win+Alt+S</kbd> novamente (ou arraste-os para a prateleira, ou sacuda o mouse): os arquivos selecionados entrarão na prateleira na hora.
4. **Arrastar tudo de uma vez**: Puxe a alça `[⤹ Total X itens]` na parte inferior para exportar todos os arquivos com um único movimento para a janela de destino ou comunicador. Quando vazia, a prateleira esmaece e fecha automaticamente.

---

## Recursos principais

### Prateleira de trânsito minimalista (experiência DropPoint)
* **Exportação em lote com um gesto**: Arraste a alça com o contador para transferir todos os arquivos de uma só vez para o Explorador, chats de mensagens ou formulários da web.
* **Fechamento automático quando vazia**: Os arquivos transferidos são removidos da prateleira; ao ficar vazia, a janela fecha suavemente sem ação manual.
* **Abertura junto ao cursor**: O atalho global identifica em qual monitor o mouse está posicionado e abre a prateleira diretamente lá.
* **Sacudir o mouse para guardar**: Ao arrastar arquivos no Explorador, sacuda o mouse levemente para abrir a prateleira e guardar a seleção no local.
* **Botão flutuante magnético**: Um pequeno botão na borda da tela que se expande com um clique ou arraste curto e se fixa magneticamente à borda.
* **Múltiplas prateleiras independentes**: Dê um duplo clique no ícone da bandeja ou pressione <kbd>Ctrl+Shift+N</kbd> para criar gavetas temporárias independentes; gavetas vazias são limpas automaticamente.

### Custódia de ramos Git e restauração segura
* **Reconhecimento do repositório**: Arquivos de repositórios Git mantêm o vínculo com a raiz; ao mudar de branch, uma notificação permite restaurá-los à posição original com um clique.
* **Inspeção de arquivos em uso**: Usa o Gerenciador de Reinicialização do Windows para detectar se há arquivos abertos (no Word, Excel ou IDEs) antes de gravar.
* **Instantâneos seguros antes de sobrescrever**: Cria cópias de segurança em um armazenamento Blob local antes de qualquer substituição, com reversão instantânea em um clique.
* **Comparação de hash de conteúdo**: Ignora gravações quando os arquivos são idênticos em bytes, preservando a data de modificação original e evitando recompilações desnecessárias.

### Bancada de trabalho completa
* **Quatro modos de visualização**: Árvore (hierárquica), lista simples, agrupado por pasta de origem e blocos de ícones grandes, com estado memorizado.
* **Modos de restauração flexíveis**: Local original, diretório especificado ou mapeamento para nova raiz; opções para caminho relativo completo, apenas nome ou estrutura achatada.
* **Pré-visualização de conflitos**: Revise os detalhes das ações antes de confirmar, com políticas de Perguntar, Sobrescrever, Ignorar e Renomear automaticamente.
* **Histórico de operações**: Registros completos para cada lote aplicado; em caso de falha parcial, você pode tentar novamente apenas os itens com erro.

### 18 idiomas nativos e temas
* Suporte nativo para 18 idiomas (100% de paridade de chaves comprovada por testes) e suporte completo a layout espelhado RTL para o árabe (ar-SA).
* Perfeita integração com os modos claro e escuro do Windows através de tons neutros de grafite.

---

## Atalhos de teclado frequentes

| Atalho | Escopo | Descrição |
| :--- | :--- | :--- |
| <kbd>Win+Alt+S</kbd> | Global | Abre a mini-prateleira; se o Explorador estiver em foco com arquivos selecionados, guarda-os diretamente |
| <kbd>Win+Alt+W</kbd> | Global | Abre a bancada de trabalho completa |
| <kbd>Win+Alt+V</kbd> | Global | Adiciona à prateleira os arquivos que estão na área de transferência |
| <kbd>Ctrl+V</kbd> | Na janela | Cola os arquivos da área de transferência na prateleira ativa |
| <kbd>Ctrl+Shift+N</kbd> | Na janela | Cria uma nova prateleira vazia independente |
| <kbd>F11</kbd> / <kbd>Ctrl+E</kbd> | Na janela | Alterna entre a mini-prateleira e a bancada de trabalho completa |
| <kbd>Esc</kbd> | Na janela | Na bancada, encolhe para a prateleira; na prateleira, recolhe para a bandeja do sistema |
| <kbd>Delete</kbd> | Lista | Remove os itens selecionados da prateleira |

> [!TIP]
> Todos os atalhos globais podem ser modificados ou desativados nas configurações. Se houver conflito com outro programa, o StashBay emitirá um aviso claro.

---

## Parâmetros de linha de comando (CLI)

O `StashBay.exe` disponibiliza comandos de terminal para integração com inicializadores externos, scripts ou automações:

| Parâmetro | Descrição |
| :--- | :--- |
| `--add <caminhos...>` | Adiciona um ou mais arquivos ou pastas à prateleira |
| `--group <nome>` | Usado junto com `--add` para especificar a prateleira de destino (criada se não existir) |
| `--apply-here <diretório>` | Grava os arquivos da prateleira no diretório indicado |
| `--apply-original` | Restaura os arquivos em seu caminho relativo original (ideal para Git) |
| `--show` | Abre e traz a janela principal para o primeiro plano |
| `--minimized` | Inicia minimizado na bandeja do sistema (para inicialização automática) |
| `--quit` | Encerra a instância em execução de forma segura |
| `--unregister-shell` | Remove as entradas de menu de contexto, atalho Enviar Para e inicialização automática |

---

## Limitações conhecidas

* **Exclusivo para Windows**: Altamente integrado ao Windows Shell, composição DWM e controle de segurança; sem versão planejada para outras plataformas.
* **Instantâneos em vez de atalhos**: Ao guardar arquivos, cópias do conteúdo são criadas na hora; modificações posteriores nos arquivos de origem não alteram a prateleira.
* **Mecanismo de instância única**: Execuções adicionais repassam os argumentos para o processo principal já ativo na bandeja, sem gerar processos órfãos.
* **Inicialização como administrador**: Executa por padrão com privilégios normais. Se precisar arrastar arquivos para janelas com privilégios elevados, ative a inicialização como administrador nas configurações e aprove a solicitação do UAC. [Guia de configuração de inicialização](../admin-startup.md).

---

## Sobre o Plano 365 Open Source

Projeto **#048** do [Plano 365 Open Source](https://github.com/rockbenben/365opensource) — uma pessoa + IA, mais de 300 projetos open-source em um ano.

[Envie sua ideia →](https://365.aishort.top/) · [Discord](https://discord.gg/PZTQfJ4GjX) · [Telegram](https://t.me/aishort_top)

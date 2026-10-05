# Decisões técnicas

## Decisões iniciais

### D-001 — Plataforma prioritária: Windows

**Estado:** aceita para o MVP.

O foco inicial é Windows, pois o uso principal envolve atalhos globais e clipboard do sistema. A lógica de transformação deve continuar independente das APIs do Windows.

### D-002 — Linguagem e runtime: C# com .NET 10

**Estado:** aceita.

Usar C# com .NET 10. .NET 10 é uma versão LTS com suporte previsto até novembro de 2028 ([política de suporte do .NET](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)).

### D-003 — Interface desktop: WPF

**Estado:** adotada para o setup inicial.

Usar WPF sobre `net10.0-windows` para a aplicação residente, a paleta e a configuração. WPF é suportado no .NET 10 ([novidades do WPF no .NET 10](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100)) e é uma opção direta para um utilitário Windows. A experiência visual da paleta será customizada e inspirada no fluxo do Raycast.

### D-004 — Atalhos globais

**Estado:** adotada.

Usar a API nativa `RegisterHotKey`/mensagem `WM_HOTKEY` ou abstração equivalente. O registro pode falhar se a combinação estiver em uso; o conflito deve ser visível e recuperável.

### D-005 — Primeiro tipo de entrada: texto Unicode

**Estado:** adotada para o MVP.

O MVP lê e escreve texto Unicode. Outros formatos ficam para uma fase posterior, com comportamento explícito por tipo.

### D-006 — Substituição de seleção usa recortar e colar

**Estado:** aceita.

Para substituir uma seleção, enviar `Ctrl+X`, transformar o texto recortado e enviar `Ctrl+V`. A entrada recortada deve permanecer em memória para recuperação em caso de falha antes da colagem. A automação pode usar `SendInput` ou mecanismo equivalente. O Windows limita a injeção de entrada entre níveis de integridade por UIPI, então aplicativos executados com privilégios superiores podem não aceitar a sequência ([SendInput — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)).

### D-007 — Paleta de comandos

**Estado:** aceita como requisito do MVP.

Abrir uma paleta pesquisável por um atalho global configurável. Ela lista comandos, mostra as combinações e permite alterá-las. Quando o Windows recusar um registro, indicar que a combinação está indisponível, sem presumir que é possível identificar qual aplicativo a ocupa.

### D-008 — Atalhos padrão iniciais

**Estado:** aceitos como padrões configuráveis.

- Abrir paleta/configuração: `Win+J`.
- Transformar clipboard: `Ctrl+Alt+I`.
- Transformar e substituir seleção: `Ctrl+Shift+I`.

`Win+J` pode conflitar com o Recall do Windows em dispositivos compatíveis: a lista atual de atalhos da Microsoft o associa a abrir Recall ([Keyboard shortcuts in Windows](https://support.microsoft.com/en-us/windows/keyboard-shortcuts-in-windows-dcc61a57-8ff0-cffe-9796-cb9706c75eec)). Portanto, esse é um padrão desejado, não uma garantia de registro; detectar indisponibilidade e permitir reconfiguração é requisito.

### D-009 — Conversão de lista para literais SQL `IN`

**Estado:** adotada para o MVP.

Reconhecer vírgula, pipe, ponto e vírgula ou quebras de linha (CRLF/LF/CR), separar os valores, remover espaços externos, escapar apóstrofos duplicando-os (`O'Brien` → `O''Brien`) e envolver cada valor em apóstrofos simples. Juntar os valores por vírgula sem delimitador final. Se não houver delimitador, tratar o texto não vazio como um único item. Se a detecção for ambígua, retornar erro e permitir que o usuário escolha um delimitador, em vez de produzir SQL possivelmente incorreto.

Regra adotada para itens vazios: ignorar separadores sobrando nas extremidades e rejeitar itens vazios entre separadores consecutivos, para evitar alterar silenciosamente a intenção. O usuário pode definir um delimitador preferido na tela de gerenciamento quando a detecção automática empatar.

### D-010 — Ferramenta de instalação: NSIS

**Estado:** aceita para a tarefa de empacotamento.

Usar NSIS (Nullsoft Scriptable Install System) para gerar instalador e desinstalador Windows reproduzíveis. O projeto o descreve como gratuito para qualquer uso; sua licença principal permite uso comercial, com condições de atribuição e preservação de avisos. Revisar os componentes de compressão usados ao distribuir o instalador. [Documentação e licença oficial do NSIS](https://nsis.sourceforge.io/Docs/Chapter1.html).

## Pendências para validar antes da distribuição

- Qual versão mínima do Windows será suportada?
- Deve ser possível atribuir a mesma combinação a dois comandos? A interface atualmente impede duplicatas ao editar.
- A notificação de sucesso será toast do Windows, ícone da bandeja ou ambas?
- A instalação oferecerá inicialização automática?
- Validar combinações e substituição de texto nas aplicações Windows alvo.

## Avaliação de viabilidade

O fluxo proposto é viável tecnicamente. A documentação oficial do Windows descreve `RegisterHotKey` como registro de atalho de sistema e envio de `WM_HOTKEY` à aplicação ([Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)). As APIs de clipboard permitem consultar, obter e definir formatos, mas exigem abertura correta do clipboard e tratamento de indisponibilidade ([Clipboard - Win32 apps](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard), [Clipboard Operations](https://learn.microsoft.com/windows/win32/dataxchg/clipboard-operations)).

As principais condições são selecionar combinações não ocupadas, começar com texto simples e impedir que uma falha grave saída vazia ou incompleta. A substituição de seleção funciona nas aplicações que aceitam entrada sintetizada e respondem a `Ctrl+X`/`Ctrl+V`; aplicativos elevados podem bloquear a automação por UIPI. Esses comportamentos precisam ser confirmados no protótipo.

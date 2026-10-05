# UC-004 — Transformar e substituir o texto selecionado

## Ator

Desenvolvedor usando um editor ou outro aplicativo Windows compatível.

## Pré-condições

- DevToolbox está em execução e a ferramenta de substituição está habilitada.
- O usuário selecionou texto editável na aplicação de origem.
- O atalho global foi registrado.

## Fluxo principal

1. O usuário seleciona um trecho de texto.
2. O usuário pressiona o atalho da ferramenta.
3. DevToolbox mantém a aplicação de origem em foco e envia `Ctrl+X` para recortar a seleção.
4. DevToolbox aguarda a atualização do clipboard e lê o texto recortado.
5. A transformação é executada sobre o texto recortado.
6. Se a transformação for bem-sucedida, DevToolbox grava o resultado no clipboard.
7. DevToolbox envia `Ctrl+V` à aplicação que recebeu a seleção.
8. O conteúdo colado substitui a seleção original; uma notificação discreta confirma o resultado.

## Por que copiar em vez de recortar

Atalho padrão para esse fluxo: `Ctrl+Shift+I`. O usuário confirmou a sequência `Ctrl+X; transformação; Ctrl+V`. Recortar remove a seleção antes de a transformação terminar, portanto a implementação precisa preservar a entrada recortada e recolocá-la na aplicação se qualquer passo anterior à colagem falhar. `Ctrl+C` seria mais seguro por manter a seleção até o resultado estar pronto, mas o fluxo inicial seguirá o recorte solicitado.

## Alternativas e falhas

- **A1 — Nenhum texto selecionado:** não transformar nem colar; informar que é necessário selecionar texto.
- **A2 — A aplicação não atualiza o clipboard após `Ctrl+X`:** não colar o valor anterior; tentar restaurar o texto original recortado.
- **A3 — Clipboard ocupado ou sem texto:** cancelar sem substituir a seleção.
- **A4 — Transformação falha:** restaurar o texto original recortado no clipboard e tentar colá-lo de volta à seleção/local de origem; informar se a restauração não puder ser concluída.
- **A5 — Foco saiu da aplicação de origem:** cancelar a colagem para evitar inserir o resultado em outra janela, quando a mudança de foco for detectável.
- **A6 — Aplicação ignora entrada sintetizada ou bloqueia a automação:** informar que a operação não foi concluída; não alegar sucesso.

## Pós-condições

- Em sucesso, o texto selecionado é substituído pelo resultado transformado.
- O clipboard contém o resultado transformado após a operação.
- Em falha anterior à colagem, o texto recortado é restaurado à aplicação de origem sempre que ela continuar disponível e aceitar entrada sintetizada.

## Critérios de aceite

- O fluxo funciona sem trazer a janela do DevToolbox para frente.
- A seleção somente é substituída após uma transformação bem-sucedida.
- Ausência de seleção, timeout e erro não levam à colagem de conteúdo antigo ou vazio.
- O comportamento é validado em cada aplicação declarada como compatível.

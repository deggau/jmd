# UC-004 — Transformar e substituir o texto selecionado

## Ator

Pessoa usando um editor ou outro aplicativo Windows compatível.

## Pré-condições

- JMD está em execução e o comando está habilitado.
- Há texto editável selecionado na aplicação de origem.
- O atalho global foi registrado, ou o comando foi escolhido na paleta.

## Fluxo principal

1. A pessoa seleciona um trecho de texto e aciona o comando.
2. JMD identifica a janela de origem, aguarda a liberação das teclas modificadoras e confirma que ela continua ativa.
3. JMD envia `Ctrl+C` e aguarda uma alteração no clipboard.
4. JMD lê o texto copiado e calcula a transformação em memória; a seleção original continua no aplicativo.
5. Se a transformação falhar, JMD informa o erro e restaura o clipboard anterior quando havia texto disponível.
6. Se a transformação for bem-sucedida, JMD grava e confirma o resultado no clipboard.
7. Se a janela de origem ainda estiver ativa, JMD envia `Ctrl+V` para substituir a seleção.
8. Uma notificação informa o resultado. Se a colagem falhar, a seleção original permanece e o resultado transformado fica no clipboard para colagem manual.

## Alternativas e falhas

- **A1 — Nenhum texto selecionado:** comandos que permitem fallback podem transformar o clipboard e informar isso; comandos exclusivos de seleção cancelam sem alterar o clipboard.
- **A2 — A aplicação não atualiza o clipboard após `Ctrl+C`:** não enviar `Ctrl+V`; usar fallback de clipboard somente quando o comando permitir.
- **A3 — Clipboard ocupado ou seleção sem texto Unicode:** não substituir a seleção; informar o limite e restaurar o clipboard anterior quando possível.
- **A4 — Transformação falha:** manter a seleção original no editor e restaurar o clipboard anterior quando havia texto disponível.
- **A5 — Foco sai da aplicação de origem:** não colar em outra janela; manter a seleção original.
- **A6 — A colagem falha ou é ignorada:** manter a seleção original, deixar o resultado no clipboard e instruir a colagem manual.
- **A7 — Aplicação elevada, protegida ou incompatível:** informar que a automação não foi concluída, sem declarar sucesso.

## Pós-condições

- Em sucesso, o texto selecionado é substituído pelo resultado transformado e o clipboard contém esse resultado.
- Antes da colagem, qualquer falha mantém o texto original no aplicativo.
- Se a colagem final falhar, o original continua selecionado e o resultado transformado permanece disponível no clipboard.
- Ao falhar a transformação, o clipboard anterior é restaurado quando seu texto foi capturado.

## Critérios de aceite

- O fluxo funciona sem trazer a janela do JMD para frente.
- JMD copia a seleção; não envia `Ctrl+X`.
- Ausência de seleção, timeout, erro de conversão ou mudança de foco não levam à substituição por conteúdo antigo ou vazio.
- O resultado só é colado após transformação e confirmação do clipboard.
- Compatibilidade é registrada por aplicação e ambiente na [matriz de validação no Windows](../testes/validacao-windows.md).

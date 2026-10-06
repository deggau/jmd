# UC-001 — Transformar texto do clipboard por atalho global

## Ator

Desenvolvedor usando qualquer aplicação Windows.

## Pré-condições

- JMD está em execução.
- A ferramenta está habilitada e seu atalho foi registrado.
- O clipboard contém texto compatível.

## Fluxo principal

1. O usuário copia um texto em qualquer aplicação.
2. O usuário pressiona o atalho associado à transformação (padrão inicial para SQL `IN`: `Ctrl+Shift+I`).
3. JMD recebe o evento global sem trazer sua janela para frente.
4. A aplicação lê o texto e executa a transformação.
5. A aplicação coloca o resultado no clipboard.
6. A aplicação mostra uma confirmação discreta.
7. O usuário cola o resultado na aplicação original ou em outra.

## Alternativas e falhas

- **A1 — Atalho não registrado:** informar conflito e orientar a escolha de outra combinação.
- **A2 — Clipboard sem texto compatível:** manter o conteúdo e informar que a ferramenta espera texto.
- **A3 — Clipboard ocupado:** tentar novamente por período curto; se continuar indisponível, preservar o dado e notificar.
- **A4 — Transformação falha:** não gravar resultado parcial; manter a entrada e mostrar erro acionável.
- **A5 — Clipboard muda durante o processamento:** evitar sobrescrever conteúdo que claramente já foi substituído por outro processo quando isso puder ser detectado.

## Pós-condições

- Em sucesso, o clipboard contém o texto transformado.
- Em falha, o usuário recebe informação e o texto original permanece sempre que tecnicamente possível.

## Critérios de aceite

- A combinação funciona com JMD em segundo plano.
- Um texto copiado é transformado e pode ser colado imediatamente.
- Falhas não produzem substituição silenciosa por texto vazio ou parcial.

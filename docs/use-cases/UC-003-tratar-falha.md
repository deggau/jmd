# UC-003 — Tratar erro de entrada ou clipboard ocupado

## Ator

Usuário de JMD.

## Fluxo

1. O usuário aciona uma ferramenta.
2. JMD tenta ler o clipboard.
3. Se a leitura não for possível, a aplicação faz tentativas limitadas e não bloqueantes.
4. Se não houver texto ou a leitura continuar falhando, a aplicação encerra a operação sem gravar um resultado.
5. A aplicação informa a causa de maneira curta e permite tentar novamente.

## Critérios de aceite

- O aplicativo permanece responsivo.
- Clipboard indisponível não causa travamento ou substituição por conteúdo vazio.
- A mensagem não inclui o texto copiado.

# UC-002 — Configurar ou trocar um atalho

## Ator

Usuário de DevToolbox.

## Fluxo principal

1. O usuário abre a configuração pela bandeja do sistema.
2. Seleciona uma ferramenta e inicia a captura de combinação.
3. Pressiona a nova combinação desejada.
4. DevToolbox tenta registrar a combinação global.
5. Em caso de sucesso, persiste a configuração e indica que está ativa.

## Alternativas

- Se a combinação estiver ocupada ou for recusada pelo sistema, a ferramenta mantém o estado anterior e explica o problema.
- Se o usuário cancelar a captura, nenhuma configuração é alterada.

## Critérios de aceite

- Combinações ativas aparecem na configuração.
- Conflitos são detectados e não deixam a ferramenta em estado ambíguo.
- Mudanças persistem após reiniciar o aplicativo.

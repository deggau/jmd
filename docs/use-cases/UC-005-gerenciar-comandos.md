# UC-005 — Abrir e gerenciar comandos na paleta

## Ator

Usuário de DevToolbox.

## Pré-condições

- DevToolbox está em execução.
- O atalho global da paleta está configurado e registrado com sucesso (padrão inicial: `Win+J`).

## Fluxo principal: localizar e executar um comando

1. O usuário pressiona o atalho global da paleta (`Win+J` por padrão) enquanto trabalha em outra aplicação.
2. A paleta compacta abre sobre a tela e recebe foco.
3. A lista de comandos disponíveis é exibida; o usuário pode digitar para filtrar por nome ou descrição.
4. O usuário navega até um comando e o executa.
5. A paleta fecha e a ação é executada conforme seu modo: usar o clipboard atual ou operar sobre a seleção na janela de origem.

## Fluxo alternativo: configurar um atalho

1. O usuário abre a área de gerenciamento de atalhos na paleta/configurações (paleta: `Win+J`; conversão do clipboard: `Ctrl+Alt+I`; substituir seleção: `Ctrl+Shift+I`, padrões iniciais).
2. Seleciona o comando e inicia a captura de uma combinação.
3. O sistema tenta registrar a nova combinação global.
4. Se aceita, mostra o estado ativo e persiste a configuração.
5. Se recusada, mantém o estado anterior (se ainda válido), marca a nova combinação como indisponível e permite tentar outra.

## Alternativas e falhas

- **A1 — Atalho da própria paleta indisponível:** DevToolbox indica o estado na área de notificação/configuração e oferece uma combinação alternativa. Sem um atalho registrado não é possível abrir a paleta pelo teclado.
- **A1a — `Win+J` inicia o Recall:** em dispositivos Windows compatíveis, o atalho pode estar reservado pelo sistema; indicar indisponibilidade e orientar a troca.
- **A2 — Combinação do comando em uso:** mostrar “atalho indisponível” sem afirmar qual aplicativo o utiliza.
- **A3 — Usuário cancela captura:** nenhuma alteração é persistida.
- **A4 — Paleta acionada em modo de substituição da seleção:** guardar a janela de origem e restaurar o foco nela ao iniciar a sequência de copiar e colar.

## Critérios de aceite

- O atalho abre a paleta sem encerrar ou desativar a aplicação de origem.
- O usuário consegue filtrar comandos e ver as combinações associadas.
- O usuário consegue configurar atalhos e entende quais estão ativos ou indisponíveis.
- O usuário consegue desativar temporariamente um comando, mantendo sua configuração, e tentar registrar novamente combinações indisponíveis.
- Fechar a paleta devolve o foco à janela anterior nos fluxos que atuam sobre seleção.

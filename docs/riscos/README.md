# Riscos e mitigação

| Risco | Efeito | Mitigação inicial |
| --- | --- | --- |
| Combinação global já em uso | A ferramenta não pode ser acionada | Validar registro, reportar conflito e permitir reconfigurar |
| Clipboard temporariamente aberto por outro app | Falha intermitente na leitura ou escrita | Tentativas curtas e limitadas; mensagem para tentar novamente |
| Clipboard com formato inesperado | Entrada não compreendida ou resultado incompatível | MVP limitado a texto Unicode; detectar e explicar formatos não suportados |
| Sobrescrita concorrente | Perda do conteúdo copiado por outro processo | Manter entrada em memória e checar mudanças quando possível antes de gravar |
| Transformação destrutiva indesejada | Texto convertido de forma inesperada | Feedback claro, ação reversível pelo clipboard enquanto não houver nova cópia e ferramenta facilmente identificável |
| Log expõe conteúdo sensível | Vazamento local em diagnóstico | Nunca registrar texto processado por padrão |
| Comportamento diferente em sessão remota/elevada | Atalho ou clipboard falha em alguns ambientes | Testar ambientes alvo e documentar suporte |
| Aplicação não aceita os comandos de teclado sintetizados | Seleção não é copiada ou resultado não é colado | Detectar ausência de atualização do clipboard, cancelar com segurança e documentar aplicações compatíveis |
| Recorte remove a seleção antes da transformação | Perda de texto caso a operação falhe | Manter a seleção recortada em memória e restaurá-la no clipboard/editor se leitura ou transformação falhar; avaliar modo alternativo com `Ctrl+C` |
| Win+J já está associado ao Recall | A paleta pode não abrir pelo atalho padrão | Detectar falha do registro e instruir o usuário a escolher outra combinação |
| Delimitador da lista é ambíguo | SQL gerado pode separar valores incorretamente | Escolher apenas delimitador dominante único; pedir configuração se houver empate |
| Clipboard contém dado anterior útil | Conteúdo do clipboard é substituído durante o fluxo | Guardar o valor anterior quando possível; definir e documentar comportamento de sucesso e recuperação |
| Foco muda durante a operação | O texto pode ser colado na janela errada | Manter operação curta, não ativar a UI própria e cancelar se a janela de origem deixar de ser válida |
| Aplicação fecha inesperadamente | Ferramentas deixam de responder | Indicador de estado na bandeja e inicialização/configuração confiável |

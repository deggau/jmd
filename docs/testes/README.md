# Estratégia de testes

Os testes unitários ficam em `tests/DevToolbox.Tests` e cobrem lógica independente de Windows. As fronteiras de clipboard, teclado e temporização são substituídas por fakes nos testes de orquestração.

## Cobertura por caso de uso

- **UC-001 — Transformar clipboard:** leitura, escrita de resultado, clipboard sem texto, falha de transformação e falha ao gravar sem sobrescrever a entrada.
- **UC-002 — Configurar atalho:** aliases, combinações padrão, letras, números, teclas especiais e rejeição de combinações inválidas. O registro real e detecção de conflitos dependem do Windows e são validados manualmente.
- **UC-003 — Tratar falhas:** rejeição de entradas vazias/ambíguas e preservação do conteúdo em erro. Repetições reais do clipboard ocupado pertencem ao adaptador Windows e exigem validação Windows.
- **UC-004 — Substituir seleção:** recortar-transformar-colar, restauração após erro de conversão, corte recusado e ausência de alteração confirmada no clipboard.
- **UC-005 — Gerenciar comandos:** valores de atalho exibidos usam o mesmo parser coberto em UC-002. A interação visual WPF e os atalhos globais exigem validação manual numa sessão Windows.
- **UC-006 — Formatar SQL IN:** cada delimitador (CRLF/LF/CR, vírgula, pipe, ponto e vírgula), espaços, separadores nas extremidades, apóstrofos, valor único, entradas vazias, vazios internos, ambiguidade e delimitador preferido.

Executar a suíte com `dotnet test DevToolbox.sln`. Os testes não substituem a validação de integração em Windows real, particularmente para `RegisterHotKey`, `SendInput`, clipboard WPF e compatibilidade por aplicativo.

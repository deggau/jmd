# Arquitetura inicial

## Direção proposta

Aplicação desktop residente para Windows, organizada em camadas, usando C#/.NET 10. WPF está proposto para a interface do MVP e será usado no setup inicial, mantendo a escolha revisável.

## Componentes

1. **Interface, paleta e bandeja do sistema**: paleta de comandos acionada por atalho, busca, configuração de ferramentas, status e mensagens. A paleta recebe foco ao ser aberta; as ações da seleção continuam executando sobre a janela previamente ativa quando aplicável.
2. **Gerenciador de atalhos**: registra e remove combinações globais; converte notificações do Windows em comandos internos; expõe o estado de registro para a interface.
3. **Serviço de clipboard**: lê e grava texto, aplica tentativas limitadas quando ocupado e encapsula integração de plataforma.
4. **Automação de entrada**: envia copiar/colar à janela ativa sem trazer a própria aplicação para frente; deve encapsular a API de entrada do Windows.
5. **Catálogo de ferramentas**: associa identificador, nome, descrição, parâmetros, atalho, modo de execução e implementação.
6. **Motor de transformação**: recebe texto e configuração, devolve texto ou erro; não acessa UI, teclado ou clipboard.
7. **Persistência local**: guarda preferências, atalhos e opções do usuário.
8. **Notificações e diagnóstico**: informa o resultado; logs técnicos não devem incluir o conteúdo processado.

## Fluxo principal

```text
Atalho global → comando da ferramenta → leitura do clipboard → transformação em memória
                                                       ↓ sucesso
                                              gravação do resultado
                                                       ↓
                                           notificação discreta
```

Para atuar sobre uma seleção, o fluxo inicial usa `Ctrl+X` → leitura e transformação → gravação do resultado → `Ctrl+V`. A janela de origem deve manter o foco durante toda a sequência. Como o recorte remove o original antes de validar o resultado, manter o texto recortado em memória e restaurá-lo se qualquer etapa falhar.

Em caso de erro na leitura ou transformação, o fluxo termina sem substituir o clipboard.

## Separação sugerida de projetos/módulos

Os nomes são indicativos; a estrutura física será escolhida ao iniciar o código.

- `DevToolbox.App`: inicialização, interface, bandeja e composição.
- `DevToolbox.Core`: modelos, configuração e contratos de transformação.
- `DevToolbox.Windows`: atalhos globais e acesso ao clipboard nativo.
- `DevToolbox.Tools`: transformações incorporadas.

## Paleta de comandos

A tela deve abrir e receber foco ao ser acionada pelo atalho global próprio. Deve apresentar busca e uma lista navegável de comandos, com nome, descrição curta e combinação associada. A configuração de atalhos deve indicar combinações indisponíveis e oferecer captura de uma nova combinação. Ao fechar a paleta, a aplicação deve devolver o foco à janela que estava ativa antes da abertura, para que os fluxos de transformação de seleção possam operar sobre ela.

O sistema operacional permite verificar se o registro do atalho foi aceito ou recusado; a interface deve comunicar indisponibilidade sem afirmar que identificou qual processo possui a combinação.

Atalhos padrão iniciais: `Win+J` abre a paleta, `Ctrl+Alt+I` transforma o clipboard e `Ctrl+Shift+I` transforma e substitui a seleção. Todos devem ser configuráveis. `Win+J` pode ser reservado pelo Recall em dispositivos compatíveis com Windows 11; a paleta deve refletir o estado real do registro.

## Transformação SQL `IN`

Implementar como ferramenta pura do núcleo: recebe texto e opção de delimitador, retorna lista validada e texto formatado ou erro. O adaptador de clipboard não deve conhecer regras SQL. Normalizar CRLF/CR, detectar delimitador dominante, rejeitar empate sem escolha, aparar itens, escapar apóstrofos internos e unir com vírgula. A montagem deve usar `string.Join` sobre os valores já formatados para garantir ausência de vírgula final.

## Extensibilidade

Começar com ferramentas incorporadas e uma interface interna pequena para adicionar novas transformações. Plugins externos e scripts só devem ser considerados após requisitos de segurança, atualização e isolamento.

## Decisões que precisam ser fechadas antes do código

- Confirmar WPF como framework da interface do MVP.
- Versões mínimas do Windows suportadas.
- Conjunto das primeiras transformações.
- Modelo de configuração da combinação de teclas e comportamento de conflito.
- Tecnologia para enviar entrada de teclado sintetizada e critérios para detectar se a cópia da seleção terminou.
- Se haverá instalador, inicialização com o Windows e atualizações automáticas na primeira entrega.

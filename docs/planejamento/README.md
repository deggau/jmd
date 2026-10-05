# Plano inicial

## Fase 0 — Descoberta e definição

- Confirmar público e fluxo prioritário.
- Escolher a lista de transformações da primeira versão.
- Especificar e revisar a conversão de lista para literais SQL `IN`, incluindo entradas ambíguas e vazias.
- Definir versões mínimas do Windows e a stack desktop.
- Fechar requisitos de privacidade, configuração e distribuição.

**Saída:** requisitos e decisões técnicas aprovados, sem dúvidas de escopo que alterem a base da aplicação.

## Fase 1 — Protótipo técnico

- Criar uma aplicação residente mínima.
- Registrar um atalho global.
- Ler texto Unicode e substituí-lo no clipboard.
- Demonstrar uma transformação simples.
- Demonstrar a substituição da seleção com recortar, transformar e colar numa aplicação de texto compatível, incluindo recuperação em caso de falha.
- Tratar conflito do atalho e clipboard ocupado.
- Fazer um protótipo da paleta de comandos, incluindo abrir/fechar por atalho e captura de uma combinação indisponível.

**Saída:** fluxo principal demonstrado em mais de uma aplicação Windows.

## Fase 2 — MVP utilizável

- Bandeja do sistema e janela de configuração.
- Paleta pesquisável inspirada no Raycast, com estado de registro dos atalhos e configuração no próprio fluxo.
- Catálogo de transformações definido para o MVP.
- Conversor de listas para SQL `IN` com delimitadores reconhecidos e sem vírgula final.
- Modo por clipboard e modo de substituição da seleção, com limites documentados.
- Edição de atalhos, persistência local e mensagens de resultado.
- Recuperação segura de erros e documentação de uso.

**Saída:** usuário consegue configurar, usar e recuperar-se de falhas sem editar configuração manualmente.

## Fase 3 — Empacotamento e qualidade

- Definir instalador/forma de distribuição e opção de iniciar com o Windows.
- Validar em versões e aplicações alvo.
- Revisar uso de memória, logs, acessibilidade e recuperação após atualização.

**Saída:** pacote reproduzível com instruções de instalação e limitações conhecidas.

## Fase 4 — Expansão

Avaliar histórico opcional, mais tipos de conteúdo, importação/exportação de configuração, comandos encadeados e extensões externas com base no uso real.

## Marco de início de implementação

Começar a Fase 1 após decidir as pendências da Fase 0 em [decisões técnicas](../decisoes/README.md). A documentação pode evoluir; não é necessário especificar cada ferramenta futura antes do protótipo.

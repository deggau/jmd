# Visão do produto

## Resumo

JMD será uma aplicação local com ferramentas pequenas para tarefas repetitivas de desenvolvimento. O primeiro fluxo permite ler o clipboard, transformar seu conteúdo por um atalho global e substituir o clipboard pelo resultado, sem exigir que a aplicação em primeiro plano mude.

## Problema

Transformações pequenas de texto frequentemente exigem abrir outra ferramenta, alternar janelas e copiar o resultado de volta. Isso interrompe o fluxo de trabalho e se repete muitas vezes ao dia.

## Usuários

- Desenvolvedores que trabalham principalmente no Windows.
- Pessoas que usam editores, terminais, navegadores, clientes SQL e ferramentas de comunicação diferentes ao longo do dia.

## Princípios

1. A ação deve ser rápida e funcionar sobre o clipboard do sistema.
2. O usuário deve saber qual transformação cada atalho executa.
3. A falha de uma transformação não deve apagar o conteúdo original.
4. Os dados permanecem locais por padrão; nada é enviado a serviços externos sem configuração explícita.
5. A aplicação deve poder ficar na área de notificação e oferecer uma interface para configurar ferramentas e atalhos.
6. O acesso às ferramentas deve ser rápido, por uma paleta de comandos com busca, inspirada no fluxo de uso do Raycast.

## Objetivos da primeira versão

- Executar ações usando atalhos globais enquanto outra aplicação está em foco.
- Transformar texto simples e devolver o resultado ao clipboard.
- Converter listas separadas por vírgula, pipe, ponto e vírgula ou quebra de linha em literais entre apóstrofos para cláusulas SQL `IN`.
- Aplicar uma transformação à seleção atual e substituir o texto selecionado sem sair da aplicação de origem.
- Configurar e identificar atalhos e transformações.
- Exibir confirmação ou erro de forma discreta.
- Abrir uma tela de gerenciamento por atalho global, listar e buscar comandos, e configurar seus atalhos.
- Mostrar claramente quais atalhos estão ativos e quais não puderam ser registrados.
- Evitar perda silenciosa de conteúdo quando o formato não for suportado ou o clipboard estiver indisponível.

## Fora do escopo inicial

- Sincronização em nuvem ou histórico de clipboard.
- Automação de teclado além da sequência controlada de copiar e colar usada para substituir uma seleção.
- Suporte garantido a todo formato binário, imagens e objetos do clipboard.
- Execução de scripts arbitrários de terceiros sem isolamento e controles próprios.

## Critérios de sucesso

- Usuário configura um atalho e uma transformação sem editar arquivos manualmente.
- O atalho funciona em aplicações Windows comuns sem exigir que JMD esteja em primeiro plano.
- Uma transformação bem-sucedida substitui o texto copiado e permite colá-lo imediatamente.
- Uma transformação acionada sobre uma seleção substitui essa seleção nas aplicações compatíveis.
- Erros, conflito de atalhos e clipboard bloqueado são comunicados sem corromper o dado anterior.

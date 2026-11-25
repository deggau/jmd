# Visão do produto

## Resumo

JMD é uma aplicação residente para Windows que reúne ferramentas rápidas de texto, comandos personalizados, tarefas, automação por voz e integração opcional com Android. A paleta e os atalhos globais permitem executar ações sem exigir que a janela do JMD permaneça em primeiro plano.

## Problema

Transformações pequenas de texto e ações repetitivas frequentemente exigem abrir outra ferramenta, alternar janelas e copiar o resultado de volta. Isso interrompe o fluxo de trabalho e se repete muitas vezes ao dia.

## Usuários

- Desenvolvedores que trabalham principalmente no Windows.
- Pessoas que usam editores, terminais, navegadores, clientes SQL e ferramentas de comunicação diferentes ao longo do dia.

## Princípios

1. A ação deve ser rápida e funcionar sobre o clipboard do sistema ou a seleção, quando aplicável.
2. O usuário deve saber qual transformação cada comando executa.
3. A falha de uma transformação não deve apagar o conteúdo original.
4. Dados permanecem locais por padrão; serviços externos exigem configuração explícita.
5. A aplicação pode ficar na área de notificação e oferecer uma interface para configurar ferramentas e atalhos.
6. A paleta pesquisável deve facilitar o acesso às ferramentas e ações disponíveis.

## Capacidades atuais

- Transformar listas em valores SQL `IN`, alterar caixa do texto e formatar ou compactar JSON e XML.
- Gerenciar snippets, rascunhos, workflows personalizados e tarefas.
- Executar comandos por atalhos globais, pela paleta e por voz, conforme configuração.
- Consultar histórico local e ajustar sua retenção.
- Descobrir JMDs por broadcast e parear aparelhos Android com identidades de chave pública; aparelhos já aprovados podem renovar o acesso e reconectar automaticamente. O QR continua disponível como alternativa.
- Solicitar que o Windows mantenha a tela ativa durante uma atividade.
- Copiar o texto selecionado antes de transformá-lo, mantendo a seleção intacta até a colagem do resultado.

## Limites atuais

- O JMD prioriza Windows; a validação de clipboard, atalhos globais e entrada sintetizada depende de aplicativos e configurações do sistema.
- O suporte a voz e modelos locais depende dos mecanismos e modelos configurados.
- Recursos Android exigem pareamento e conectividade na rede local. O pareamento não deve ser tratado como autorização para publicar o serviço na internet.
- Workflows executam ações configuradas pelo usuário; a compatibilidade depende do programa, caminho ou endpoint usado.
- O JMD não garante suporte a todo formato binário, objeto ou conteúdo avançado do clipboard.

## Critérios de sucesso

- Usuário consegue localizar, configurar e executar um comando sem editar arquivos manualmente.
- Atalhos indisponíveis e falhas são comunicados com clareza.
- Transformações bem-sucedidas produzem o resultado esperado no clipboard ou na seleção, em aplicativos validados.
- Uma falha antes da colagem mantém a seleção original no lugar.
- Integrações de rede só executam ações após configuração e autorização correspondentes.

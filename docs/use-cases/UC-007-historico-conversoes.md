# UC-007 — Consultar o histórico de conversões

## Ator

Usuário do JMD.

## Pré-condições

- O JMD está em execução.
- O usuário executou ao menos uma conversão bem-sucedida, ou quer alterar o prazo de retenção.

## Fluxo principal

1. O usuário abre **Gerenciar atalhos** e seleciona a aba **Histórico**.
2. O JMD mostra conversões em ordem cronológica decrescente, com data/hora local, comando, valor anterior e valor convertido.
3. O usuário digita um trecho na busca.
4. O JMD filtra os registros que contêm o trecho em qualquer um dos valores.

## Fluxo alternativo: ajustar retenção

1. O usuário informa por quantos dias deseja manter os registros.
2. O JMD salva a preferência e remove imediatamente os registros anteriores ao prazo.
3. O prazo inicial é de 15 dias.

## Pós-condições

- Conversões bem-sucedidas são persistidas em SQLite local.
- Registros vencidos são removidos ao iniciar o aplicativo e ao salvar um novo prazo.

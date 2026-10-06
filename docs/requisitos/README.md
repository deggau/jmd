# Requisitos e limites

## Requisitos funcionais

- **RF-01** Manter um processo residente, com acesso à interface pela área de notificação.
- **RF-02** Registrar atalhos globais configurados pelo usuário.
- **RF-03** Detectar quando uma combinação já está ocupada e permitir escolher outra.
- **RF-04** Ler texto Unicode do clipboard quando um atalho for acionado.
- **RF-05** Executar a transformação vinculada ao atalho.
- **RF-06** Substituir o conteúdo do clipboard pelo resultado apenas quando a transformação terminar com sucesso.
- **RF-07** Informar sucesso e erro sem interromper o uso da janela atual.
- **RF-08** Permitir habilitar/desabilitar comandos incorporados e editar seus atalhos; adicionar ou remover ferramentas personalizadas fica fora do MVP.
- **RF-09** Fornecer a transformação de lista para SQL `IN` como primeira ferramenta do MVP.
- **RF-10** Permitir testar uma ação pela interface usando uma entrada de exemplo.
- **RF-11** Oferecer um modo de execução que copie a seleção da aplicação ativa, transforme o texto e cole o resultado sobre a seleção.
- **RF-12** Enviar os comandos de teclado necessários sem ativar a janela do JMD nem retirar o foco da aplicação de origem.
- **RF-13** Detectar quando a seleção não foi copiada ou o clipboard não mudou, e cancelar sem colar conteúdo inesperado.
- **RF-14** Abrir a paleta/tela de gerenciamento por um atalho global configurável.
- **RF-15** Listar e permitir buscar os comandos disponíveis na tela de gerenciamento.
- **RF-16** Permitir capturar, configurar e alterar a combinação de cada comando e da própria paleta.
- **RF-17** Mostrar o estado de cada atalho: ativo, desativado ou indisponível por conflito/erro de registro.
- **RF-18** Permitir tentar novamente o registro de um atalho após o conflito ser resolvido.
- **RF-25** Permitir habilitar/desabilitar cada comando sem remover sua configuração e oferecer uma ação para tentar registrar novamente atalhos indisponíveis.
- **RF-19** Definir como atalhos padrão `Alt+J` para a paleta, `Ctrl+Alt+I` para transformar o clipboard e `Ctrl+Shift+I` para transformar e substituir a seleção; todos devem continuar configuráveis.
- **RF-20** Incluir uma transformação que converte uma lista delimitada em valores entre apóstrofos, separados por vírgulas, para uso em cláusula SQL `IN`.
- **RF-21** Detectar delimitadores vírgula, pipe, ponto e vírgula e quebras de linha CRLF, LF ou CR.
- **RF-22** Nunca incluir vírgula depois do último valor produzido.
- **RF-23** Remover espaços externos dos itens e escapar apóstrofos internos duplicando-os antes de cercar cada item com apóstrofos.
- **RF-24** Permitir escolher detecção automática ou um delimitador preferido para listas ambíguas, salvando a preferência localmente.
- **RF-26** Registrar conversões bem-sucedidas com data/hora, comando e valores anterior e convertido em banco SQLite local.
- **RF-27** Exibir o histórico em ordem cronológica e permitir buscar por trechos dos valores anterior e convertido.
- **RF-28** Permitir configurar por quantos dias manter o histórico, usando 15 dias como padrão e removendo registros vencidos.

## Requisitos não funcionais

- **RNF-01** Windows é a plataforma prioritária da primeira versão.
- **RNF-02** A operação comum deve ter baixa latência e não bloquear a interface.
- **RNF-03** A lógica de transformação deve ser separada das APIs de Windows para facilitar testes e evolução.
- **RNF-04** A aplicação não deve registrar conteúdo do clipboard em logs técnicos; o histórico funcional de conversões fica no banco local e segue o prazo de retenção configurado.
- **RNF-05** Preferências devem persistir localmente e permitir recuperação após reinício.
- **RNF-06** O sistema deve lidar com clipboard temporariamente ocupado, dados ausentes e formatos não suportados.
- **RNF-07** Atalhos não podem assumir disponibilidade universal: outras aplicações ou o Windows podem reservar a combinação.

## Regras do fluxo de transformação

1. Ler e guardar o valor textual atual.
2. Validar o tipo de dado e limites configurados.
3. Calcular o resultado sem modificar o clipboard.
4. Se houver sucesso, gravar o resultado no clipboard.
5. Se qualquer etapa falhar, manter o valor anterior sempre que a API permitir e informar o motivo.

O clipboard é compartilhado e pode ser alterado por outro processo durante a operação. A implementação deve minimizar essa janela e verificar erros de leitura e gravação.

## Regras do fluxo de substituição da seleção

1. Guardar uma cópia do conteúdo atual do clipboard quando possível.
2. Guardar a janela de origem e enviar `Ctrl+X` à aplicação em foco.
3. Aguardar, por tempo limitado, a atualização do clipboard e ler o texto recortado.
4. Executar a transformação em memória.
5. Só em caso de sucesso, gravar o resultado e enviar `Ctrl+V` à mesma aplicação.
6. Se a leitura ou transformação falhar depois do recorte, restaurar o texto original ao clipboard e tentar colá-lo de volta na aplicação de origem; reportar falha de recuperação claramente.

O modo seleção usa `Ctrl+X` conforme solicitado, portanto remove o texto antes de sabermos se a transformação terminou bem. O texto recortado deve permanecer guardado em memória durante a operação para recuperação se necessário. Uma alternativa mais segura seria usar `Ctrl+C`, mantendo a seleção original até o resultado estar pronto; essa alternativa não será o comportamento padrão do fluxo atual.

## Limitações conhecidas

- `RegisterHotKey` dá suporte a atalhos globais, mas o registro pode falhar se a combinação estiver em uso. O aplicativo precisa apresentar esse conflito.
- O Windows informa se o registro de uma combinação falhou, mas a aplicação não deve presumir que consegue identificar com confiabilidade qual outro aplicativo a reservou. A interface deve indicar que a combinação está indisponível e permitir escolher outra.
- Aplicativos podem publicar vários formatos de clipboard ou renderizar dados sob demanda. A primeira versão deve declarar que suporta texto Unicode e tratar outros tipos com uma mensagem clara.
- Clipboard pode ficar temporariamente bloqueado por outro processo. Leitura e escrita devem ter tentativas curtas e limitadas, sem travar a interface.
- Áreas de trabalho remotas, sessões elevadas e políticas corporativas podem alterar o comportamento de atalhos e clipboard; deverão entrar na validação de compatibilidade.
- Comandos de teclado sintetizados podem ser ignorados ou tratados de modo especial por algumas aplicações, janelas elevadas, telas seguras ou campos protegidos. O recurso precisa comunicar limites e ser validado nas aplicações alvo.

# Matriz de validação manual no Windows

Esta matriz registra compatibilidade observada em Windows real. Os testes automatizados cobrem regras e lógica isoladas; eles não comprovam o comportamento do clipboard, `RegisterHotKey`, `SendInput`, firewall, captura de tela ou janelas de terceiros.

## Como registrar uma execução

Para cada execução, anote:

- Data, edição/build do Windows e arquitetura.
- Versão do JMD e versão da aplicação testada.
- Aplicação e campo/controle usado.
- Se JMD e aplicação estavam em execução normal ou elevada.
- Sessão local ou remota (RDP), layout de teclado e gerenciadores de clipboard ativos.
- Passos realizados, resultado esperado, resultado observado e evidência de falha.
- Estado: **Pendente**, **Passou**, **Falhou** ou **Bloqueado**, com observações.

Não marque uma aplicação como compatível com base apenas em outra aplicação da mesma categoria. Repita cada fluxo após reiniciar o JMD quando a persistência ou o registro de atalhos fizer parte do cenário.

## Aplicações de referência

| Aplicação | Fluxos prioritários | Estado inicial |
| --- | --- | --- |
| Bloco de Notas | Atalho global, copiar/transformar/colar seleção, clipboard | Pendente |
| Visual Studio Code | Texto em editor, seleção, atalhos do editor e conflito de atalho | Pendente |
| Visual Studio | Editor e janela executada elevada, quando aplicável | Pendente |
| Windows Terminal | Seleção e colagem no prompt, sem execução acidental de comando | Pendente |
| Edge ou Chrome | Campos de formulário e editor web com conteúdo rico | Pendente |
| Microsoft Word | Inserção de snippet e formatos ricos HTML/RTF, se disponíveis | Pendente |

## Matriz de cenários

| Área | Cenário | Resultado esperado | Estado inicial |
| --- | --- | --- | --- |
| Atalhos | Abrir paleta e executar comandos com cada atalho padrão | Janela correta recebe o comando; conflito é informado e pode ser reconfigurado | Pendente |
| Seleção | Transformar seleção válida em cada aplicação de referência | `Ctrl+C` captura a seleção; o resultado transformado substitui somente a seleção original | Pendente |
| Seleção | Forçar erro de transformação ou copiar conteúdo sem texto Unicode | Seleção original permanece no editor; nenhum conteúdo antigo é colado por engano | Pendente |
| Seleção | Mudar o foco durante o processamento | Resultado não é colado em outra janela; mensagem explica a falha | Pendente |
| Seleção | Bloquear a colagem ou fazer a aplicação ignorar `Ctrl+V` | Seleção original permanece; resultado transformado fica no clipboard para colagem manual | Pendente |
| Clipboard | Clipboard ocupado, substituído por outro processo ou com formato não suportado | Operação falha com mensagem clara e sem declarar sucesso indevido | Pendente |
| Snippets | Salvar seleção, buscar pelo título e inserir texto simples | Conteúdo correto é inserido na aplicação que estava ativa | Pendente |
| Snippets ricos | Inserir conteúdo HTML/RTF em aplicações que aceitam esses formatos | Formatação disponível é preservada; aplicações sem suporte recebem fallback adequado | Pendente |
| Workflows | Abrir programa/URL e executar sequência configurada | Etapas executam na ordem configurada; falhas são visíveis e não deixam estado incorreto | Pendente |
| Tarefas | Criar, editar, concluir e reabrir tarefa com checklist, lembrete e anexo | Alterações são persistidas e os itens continuam associados à tarefa correta | Pendente |
| Persistência | Reiniciar JMD após alterar atalhos, snippets, workflows, tarefas e retenção do histórico | Preferências e dados locais permanecem consistentes; itens vencidos seguem a retenção configurada | Pendente |
| Voz | Acionar comando e transcrever fala com os mecanismos configurados | Comando ou texto reconhecido corresponde à configuração; ausência de modelo é informada | Pendente |
| Rede Android | Enviar broadcast com nonce válido e inválido, em rede privada e fora dela | JMD responde unicast só a pedido válido da rede privada, repetindo nonce e assinando a identidade; datagrama malformado é ignorado | Pendente |
| Android | Descobrir por broadcast, validar nonce/assinatura, aprovar pedido e reconectar com a mesma chave | Só identidades aprovadas reconectam sem novo aceite; chave diferente exige aprovação | Pendente |
| Android | Repetir o mesmo `requestId`, mudar a chave ou revogar o aparelho | Replay e troca de chave são recusados; revogação impede renovação automática e exige novo aceite | Pendente |
| Android | Bloquear UDP ou usar rede guest/VPN e parear pelo QR | QR continua funcionando quando broadcast não atravessa a rede | Pendente |
| Firewall | Configurar regra privada e confirmar API TCP + descoberta UDP; testar perfil Público | Regras só liberam a porta da API e UDP 47832 na sub-rede privada; perfil Público é recusado | Pendente |
| Android | Iniciar compartilhamento de tela nos dois sentidos e encerrar | Consentimento do sistema é respeitado; imagem e encerramento chegam ao outro lado | Pendente |
| Integrações | Usar Businessmap ou outro serviço externo após configurar credenciais | Falha de autenticação/rede é informada; credenciais não aparecem em logs ou notificações | Pendente |
| Energia | Ativar e desativar “Manter tela ativa” | Estado de energia é restaurado ao desligar o recurso ou fechar o JMD | Pendente |
| Ambiente | Repetir atalhos e clipboard em sessão RDP e entre níveis de privilégio | Limitações são identificadas e documentadas; nenhuma falha é apresentada como sucesso | Pendente |

## Compatibilidade publicada

Mantenha os resultados por versão do JMD e do Windows. Só liste como compatíveis no material de instalação as aplicações e cenários que passaram nesta matriz. Para falhas dependentes de aplicativo, registre a versão exata e uma alternativa segura para o usuário.

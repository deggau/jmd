# API local do JMD para o JMDroid

**Estado:** implementada no JMD desktop; o cliente Android precisa integrar este contrato.

Este documento define como o JMDroid pode parear com uma instância do JMD na mesma rede local, descobrir e executar comandos, gerenciar tarefas e consultar o histórico de tarefas. Não há servidor central: o JMD no computador executa as ações.

## Premissas

- O computador precisa estar ligado, com o JMD aberto e acessível pela rede local. O app não executa comandos do computador quando ele está desligado.
- A primeira versão aceita conexão na mesma rede local. Não se deve publicar a porta do JMD diretamente na internet.
- A API usa HTTPS por padrão. Opcionalmente, o usuário pode selecionar HTTP para uma rede local confiável; nesse modo, todo o tráfego fica sem criptografia e `certificateSha256` fica vazio. Não exponha a porta fora da rede privada.
- Com pelo menos um aparelho pareado, o JMD mantém o listener ativo enquanto o aplicativo desktop estiver aberto e inicia o serviço novamente ao abrir o JMD. Isso permite que o celular reconecte sem reabrir a tela do QR. Sem aparelhos pareados, o serviço encerra após uma hora sem atividade, salvo enquanto a tela de pareamento estiver aberta.

## Pareamento e autenticação

### 1. Abrir o pareamento no desktop

Ao abrir a tela de conexão, o JMD inicia o listener em uma porta local disponível e cria uma sessão de pareamento aleatória, válida por 2 minutos e utilizável uma única vez. HTTPS é o padrão. Com HTTP selecionado, `baseUrl` começa com `http://` e `certificateSha256` fica vazio. O QR code HTTPS contém um objeto JSON neste formato:

```json
{
  "type": "jmd-pairing",
  "version": 1,
  "baseUrl": "https://192.168.1.20:54321",
  "certificateSha256": "A1B2...64 caracteres hexadecimais...",
  "sessionId": "identificador-aleatorio",
  "pairingSecret": "segredo-aleatorio-de-uso-unico",
  "expiresAt": "2026-11-13T04:00:00Z"
}
```

O `pairingSecret` é temporário e serve apenas para iniciar o pedido. Ele não é o token de acesso do aparelho. HTTP só deve ser usado em uma rede local confiável, pois o segredo e os tokens trafegam sem criptografia.

### 2. Solicitar pareamento pelo JMDroid

```http
POST /api/v1/pairing/requests
Content-Type: application/json
```

```json
{
  "sessionId": "identificador-aleatorio",
  "pairingSecret": "segredo-aleatorio-de-uso-unico",
  "deviceName": "POCO X3",
  "appVersion": "0.3.0"
}
```

Resposta enquanto aguarda confirmação no desktop:

```http
202 Accepted
```

```json
{
  "requestId": "pedido-aleatorio",
  "state": "pending",
  "pollAfterMs": 1500
}
```

O JMD desktop mostra o nome do aparelho e os botões **Aprovar** e **Negar**. O pedido expira junto com a sessão de pareamento. O QR, por si só, nunca autoriza um aparelho.

### 3. Consultar a aprovação

```http
GET /api/v1/pairing/requests/{requestId}
X-JMD-Pairing-Secret: segredo-aleatorio-de-uso-unico
```

Resposta após aprovação:

```json
{
  "state": "approved",
  "deviceId": "device-aleatorio",
  "accessToken": "token-opaco-aleatorio-com-pelo-menos-256-bits"
}
```

O JMDroid grava o token no armazenamento protegido pelo Android Keystore e descarta o segredo de pareamento. O desktop persiste somente o hash do token, associado ao aparelho. O token é exclusivo daquele aparelho e revogável no JMD.

Estados possíveis: `pending`, `approved`, `denied`, `expired`.

## Sessão conectada

```http
GET /api/v1/session
Authorization: Bearer {accessToken}
Upgrade: websocket
```

O JMDroid mantém uma sessão WebSocket (`wss://` em HTTPS ou `ws://` em HTTP) enquanto estiver conectado. A sessão conta para o ciclo de vida do listener e permite ao desktop exibir o aparelho como conectado. Ao revogar o aparelho, o servidor invalida o token e fecha suas sessões. Uma desconexão normal encerra aquela sessão; se o aparelho continuar pareado, o listener permanece ativo para permitir reconexão.

Mensagem inicial do servidor:

```json
{
  "type": "session.ready",
  "deviceId": "device-aleatorio",
  "apiVersion": 1
}
```

O servidor envia pings WebSocket de protocolo para manter a sessão e detectar desconexões; clientes WebSocket Android respondem automaticamente no protocolo. O token deve ser enviado apenas em HTTPS/WSS no modo HTTPS. Em HTTP, ele trafega sem criptografia na rede local e nunca deve aparecer em logs.

## Compartilhar a tela do Android

### Visão geral e início pelos dois lados

A transmissão usa a sessão WebSocket autenticada já aberta em `GET /api/v1/session`. **Não abra outro socket para as imagens.** O JMD desktop recebe os quadros e os mostra em uma janela; o protocolo envia somente imagem, sem áudio, comandos de toque ou teclado remoto.

A mesma ação pode iniciar o fluxo pelo computador ou pelo celular:

1. **Computador → celular:** no JMD, o usuário escolhe **Ver tela do celular**. Se houver mais de um Android conectado, escolhe o aparelho. O JMD envia `screen_share.start` pelo WebSocket. O app Android precisa estar conectado para receber o pedido.
2. **Celular → computador:** no catálogo de comandos retornado por `GET /api/v1/commands`, o app mostra `screenShare` como **Ver tela do celular**. Ao selecionar, o app chama `POST /api/v1/commands/screenShare/execute`. O JMD responde à chamada HTTP e, em paralelo, envia o mesmo `screen_share.start` pelo WebSocket daquela sessão.

Nos dois casos, quem inicia é apenas o pedido. O Android sempre apresenta o consentimento de captura do sistema, e só começa a transmitir depois da autorização. O comando remoto não contorna a confirmação do Android.

O fluxo de estados esperado no cliente é `ocioso → pedido recebido → aguardando consentimento → transmitindo → encerrando → ocioso`. O Android pode também encerrar a transmissão a qualquer momento, por exemplo, quando o usuário a interromper pela interface do sistema.

### Iniciar pelo catálogo de comandos

O item `screenShare` aparece no catálogo com schema de entrada vazio. Execute-o como qualquer outro comando:

```http
POST /api/v1/commands/screenShare/execute
Authorization: Bearer {accessToken}
Content-Type: application/json
```

```json
{
  "requestId": "uuid-unico-por-acao",
  "input": {}
}
```

Resposta quando o pedido foi enviado ao WebSocket:

```json
{
  "requestId": "uuid-unico-por-acao",
  "commandId": "screenShare",
  "status": "succeeded",
  "result": {
    "text": "Solicitação enviada. Autorize a captura de tela no Android para iniciar a transmissão."
  },
  "error": null
}
```

`succeeded` significa somente que o JMD conseguiu enviar o pedido; **não** confirma consentimento nem que o primeiro quadro chegou. O app deve acompanhar o WebSocket para observar o início, a falha e o encerramento. Um aparelho desconectado resulta em `status: "failed"`. Gere um `requestId` novo para cada ação deliberada. Repetir o mesmo ID devolve o resultado já armazenado pelo JMD, evitando iniciar a mesma ação duas vezes.

### Pedido de início enviado pelo JMD

O Android deve estar preparado para receber o controle `screen_share.start` a qualquer momento após `session.ready`, independente de qual lado iniciou a ação:

```json
{
  "type": "screen_share.start",
  "shareId": "id-opaco-da-transmissao",
  "maxFps": 30,
  "maxDimension": 2400,
  "maxFrameBytes": 2097152,
  "format": "jpeg",
  "quality": 65
}
```

Todos os campos são definidos pelo JMD. `shareId` identifica essa transmissão e deve ser copiado sem alterações para todas as respostas de controle. `maxFps` é o limite de envio configurado no desktop (12–60 FPS; o padrão é 12); respeite-o mesmo se o codificador conseguir produzir mais quadros. `maxDimension` limita o maior lado da imagem a 2400 pixels. `maxFrameBytes` limita cada JPEG a 2.097.152 bytes (2 MiB). `format` define JPEG e `quality` é a qualidade JPEG solicitada (0–100; o valor atual do JMD é 65).

Se já houver um pedido ativo com o mesmo `shareId`, trate-o como duplicado: não abra uma segunda captura nem peça consentimento novamente. Deve haver no máximo uma transmissão ativa por aparelho.

### Consentimento e confirmação de início

Ao receber um novo `screen_share.start`, solicite a captura pelo mecanismo oficial do Android. Depois do consentimento, inicie a captura e envie **uma** mensagem de texto:

```json
{
  "type": "screen_share.started",
  "shareId": "id-opaco-da-transmissao",
  "width": 1080,
  "height": 2400,
  "rotation": 0,
  "format": "jpeg"
}
```

`width` e `height` são as dimensões do quadro JPEG que será enviado; `rotation` é a rotação atual em graus (`0`, `90`, `180` ou `270`). Envie `started` antes do primeiro quadro. Se o usuário negar a permissão ou a captura não puder começar, envie `screen_share.error` com o mesmo `shareId` e uma mensagem curta para exibição:

```json
{
  "type": "screen_share.error",
  "shareId": "id-opaco-da-transmissao",
  "message": "permissão negada"
}
```

Após `error`, libere recursos locais e volte ao estado ocioso. O JMD encerra o pedido e mostra essa mensagem na janela do aparelho.

### Envio de quadros

Depois de `screen_share.started`, envie cada quadro como **uma mensagem WebSocket binária completa**, cujo payload é somente um JPEG (sem JSON, cabeçalho próprio, tamanho ou prefixo). Não divida um JPEG em mensagens WebSocket separadas. Exemplo conceitual:

```text
WebSocket TEXT:   {"type":"screen_share.started", ...}
WebSocket BINARY: FF D8 ... FF D9   (um JPEG completo)
WebSocket BINARY: FF D8 ... FF D9   (quadro seguinte)
```

O cliente deve:

- manter o aspecto da imagem e nunca exceder `maxDimension` no maior lado;
- codificar em JPEG usando `quality` como referência e manter cada mensagem até `maxFrameBytes`;
- enviar no máximo `maxFps` quadros por segundo. Pode enviar menos para reduzir CPU, aquecimento e tráfego;
- enviar somente o quadro mais recente disponível, descartando quadros atrasados em vez de acumular uma fila crescente;
- enviar quadros apenas para o `shareId` atualmente ativo e pela mesma sessão autenticada que recebeu o pedido.

O JMD confere os marcadores de início/fim JPEG, rejeita quadros acima de 2 MiB e aplica o limite de FPS selecionado no desktop. Quadros que ultrapassam a taxa são descartados. O JMD **não decodifica nem valida as dimensões declaradas**; cabe ao cliente respeitar `maxDimension`. Uma mensagem binária maior que 2 MiB encerra o WebSocket com `MessageTooBig`; por isso, dimensione/reduza a qualidade antes de enviar.

### Parada e limpeza

Se o usuário fechar a janela do compartilhamento no desktop, o JMD envia:

```json
{ "type": "screen_share.stop", "shareId": "id-opaco-da-transmissao" }
```

Ao receber esse controle, pare a produção e o envio de quadros, encerre a sessão `MediaProjection`, libere `VirtualDisplay`, `Surface` e demais recursos e responda uma vez:

```json
{ "type": "screen_share.stopped", "shareId": "id-opaco-da-transmissao" }
```

Se o Android encerrar primeiro (usuário parou pelo sistema, bloqueio de tela, projeção revogada ou erro após iniciar), faça a mesma limpeza local e envie `screen_share.stopped`. `screen_share.error` é para falha ao iniciar; para uma sessão que já iniciou, use `screen_share.stopped`. Ao perder o WebSocket, pare a captura e libere recursos sem esperar reconexão. Ignore controles cujo `shareId` não corresponda à transmissão ativa. Não envie `screen_share.stop` em resposta a `screen_share.stop`.

### Requisitos Android e privacidade

Use `MediaProjection` e mantenha a captura em um serviço de primeiro plano pelo tempo em que ela estiver ativa, com o tipo de serviço e permissões exigidos pelo nível de API alvo. Para apps que **target Android 14 (API 34) ou superior**, a documentação exige declarar `FOREGROUND_SERVICE` e `FOREGROUND_SERVICE_MEDIA_PROJECTION`, além de um serviço com `android:foregroundServiceType="mediaProjection"`. Solicite consentimento antes de cada sessão e use o token de projeção apenas uma vez; não reutilize o token para reiniciar a captura. Registre `MediaProjection.Callback.onStop()` para limpar os recursos quando o sistema ou o usuário encerrar a projeção. Em Android 15 QPR1 ou posterior, o indicador de projeção do sistema também permite que o usuário interrompa o compartilhamento.

Consulte a documentação oficial de [MediaProjection](https://developer.android.com/media/grow/media-projection) para o fluxo de consentimento, configuração do serviço, mudanças de tamanho/rotação e limpeza. A janela local do app pode ser iniciada por uma ação do usuário; quando o pedido veio do desktop e o Android restringir abrir a tela de consentimento em segundo plano, mostre uma notificação tocável para o usuário continuar. Nunca inicie ou retome a captura silenciosamente.

## Listar e filtrar comandos

```http
GET /api/v1/commands?q=sql&includeUnavailable=false
Authorization: Bearer {accessToken}
```

`q` é opcional. O filtro segue a mesma ordem da paleta do JMD: correspondência exata, prefixo e depois ocorrência em qualquer trecho. Pesquisa nome, descrição, atalho e frases de voz. A lista é ordenada por essa classificação e, em seguida, pelo nome. A filtragem da tela Android pode ser local após obter a lista; consultar novamente com `q` também é suportado.

Resposta:

```json
{
  "items": [
    {
      "id": "jsonPretty",
      "name": "Formatar JSON",
      "description": "Formata um JSON com indentação.",
      "shortcut": "Sem atalho",
      "available": true,
      "unavailableReason": null,
      "inputSchema": {
        "type": "object",
        "properties": {
          "text": { "type": "string", "maxLength": 1000000 }
        },
        "required": ["text"]
      }
    }
  ],
  "count": 1,
  "query": "sql"
}
```

Por padrão, são retornados os comandos disponíveis e os comandos personalizados, mesmo quando a execução remota ainda está desativada. `includeUnavailable=true` também retorna os demais comandos locais que dependem de clipboard, seleção/janela em foco, diálogos ou outra interação exclusiva do desktop. Itens indisponíveis incluem `available: false` e um motivo legível. Comandos desativados nas configurações do JMD não podem ser executados remotamente.

Comandos personalizados aparecem com o mesmo `id` salvo no JMD (prefixo `custom:`) e permitem execução pelo JMDroid por padrão. Para bloquear um workflow, edite-o no JMD e marque **Bloquear execução pelo JMDroid (Android)**. Workflows podem abrir programas, digitar e interagir com janelas deste computador; bloqueie individualmente os comandos que não deseja controlar pelo celular. `inputSchema` descreve as variáveis `{{nome}}` usadas pelas etapas. Envie os valores em `input.variables`; variáveis omitidas ainda podem ser resolvidas pelas variáveis globais cadastradas no JMD.

As rotas autenticadas de comandos aceitam até 120 chamadas por minuto por aparelho. Ao exceder o limite, a API responde `429` e inclui `Retry-After`.

## Executar um comando

```http
POST /api/v1/commands/{commandId}/execute
Authorization: Bearer {accessToken}
Content-Type: application/json
```

Exemplo:

```json
{
  "input": {
    "text": "{\"nome\":\"JMD\"}"
  },
  "requestId": "uuid-gerado-pelo-app"
}
```

Resposta de execução síncrona:

```json
{
  "requestId": "uuid-gerado-pelo-app",
  "commandId": "jsonPretty",
  "status": "succeeded",
  "result": {
    "text": "{\n  \"nome\": \"JMD\"\n}"
  }
}
```

O app valida `commandId` contra o catálogo remoto e valida os campos conforme `inputSchema`. A API nunca aceita nome de executável, caminho ou linha de comando. Cada execução é atribuída ao aparelho autenticado e registrada no histórico do JMD. `requestId` serve para evitar executar duas vezes a mesma requisição em caso de repetição de rede.

Quando aplicável, o objeto `result` também contém `inputItemCount` e `outputItemCount` (por exemplo, no comando SQL IN). Uma falha de validação/execução retorna `status: "failed"` e o objeto `error` no formato documentado abaixo.

Workflows personalizados habilitados são executados no computador como se fossem acionados no JMD. Os demais comandos que dependem de interação com o desktop permanecem indisponíveis para execução remota.

## Tarefas

Todas as rotas abaixo usam `Authorization: Bearer {accessToken}` e aceitam até 120 chamadas por minuto por aparelho. As respostas de tarefa usam camelCase. Datas são ISO 8601 com fuso horário; listas de `urls`, `attachments` e `checklist` mantêm os dados editáveis no painel desktop.

### Listar e consultar

```http
GET /api/v1/tasks?status=open
GET /api/v1/tasks/{taskId}
Authorization: Bearer {accessToken}
```

`status` pode ser `open` (padrão), `completed` ou `all`. A listagem responde `{ "items": [...], "count": 1, "status": "open" }`. Cada tarefa contém `id`, `title`, `description`, `createdAt`, `completedAt`, `taskNumber`, `reminderIntervalMinutes`, `categoryColor`, `lastReminderAt`, `urls`, `attachments`, `pausedAt`, `pauseReason`, `pausedBy` e `checklist`. Cada etapa contém `title`, `weight` e `isCompleted`.

### Criar e editar

```http
POST /api/v1/tasks
PUT /api/v1/tasks/{taskId}
Authorization: Bearer {accessToken}
Content-Type: application/json
```

O corpo de criação e edição contém os mesmos campos editáveis:

```json
{
  "title": "Preparar release",
  "description": "Revisar build e publicação",
  "reminderIntervalMinutes": 30,
  "categoryColor": "Laranja",
  "urls": "https://github.com/deggau/jmd",
  "attachments": "C:\\docs\\release.txt",
  "checklist": [
    { "title": "Compilar", "weight": 50, "isCompleted": false },
    { "title": "Validar instalador", "weight": 50, "isCompleted": false }
  ]
}
```

`title` é obrigatório (até 300 caracteres); `description` aceita até 100.000 caracteres; `urls` e `attachments` até 20.000 cada; `reminderIntervalMinutes` é nulo ou de 1 a 10.080; a lista aceita até 100 etapas e a soma dos pesos não pode passar de 100. Criação retorna `201 Created`, o objeto criado e o cabeçalho `Location`. Edição retorna `200 OK` com o objeto atualizado. Criar, editar ou alterar uma tarefa é registrado no histórico.

### Enviar anexos do celular

Depois de criar a tarefa, ou para adicionar arquivos a uma tarefa existente, envie um `multipart/form-data` para a mesma rota de anexos. Cada arquivo deve usar o campo `files`; é possível repetir esse campo:

```http
POST /api/v1/tasks/{taskId}/attachments
Authorization: Bearer {accessToken}
Content-Type: multipart/form-data
```

Exemplo com `curl`:

```bash
curl -X POST "https://192.168.1.10:47831/api/v1/tasks/ID/attachments" \
  -H "Authorization: Bearer TOKEN" \
  -F "files=@/caminho/no/celular/comprovante.pdf" \
  -F "files=@/caminho/no/celular/foto.jpg"
```

O cliente Android deve criar a tarefa com `POST /api/v1/tasks`, obter o `id` da resposta e então enviar os arquivos para essa rota. A mesma chamada também aceita tarefas já criadas. Os arquivos são copiados para a pasta configurada em **Configurações > Integrações > Businessmap > Pasta independente para anexos locais**. Se ela estiver vazia, o JMD usa `%LOCALAPPDATA%\\JMD\\TaskAttachments`. Em ambos os casos, os arquivos ficam organizados em subpastas por data e código da tarefa, e os caminhos finais são adicionados à tarefa sem remover anexos anteriores.

Cada envio aceita até 10 arquivos, com no máximo 20 MiB por arquivo e 24 MiB no total. O sucesso retorna `{ "task": {...}, "uploaded": ["caminho final do arquivo"] }`. O envio gera um evento no histórico identificado como `JMDroid` e com o nome do celular. Uma tarefa inexistente retorna `404 task_not_found`; formato inválido retorna `400 invalid_input`; excesso de tamanho retorna `413 attachment_too_large`; falha ao gravar no computador retorna `500 attachment_storage_failed`.

### Etapas, conclusão e ciclo de vida

```http
PUT  /api/v1/tasks/{taskId}/checklist/{index}
POST /api/v1/tasks/{taskId}/complete
POST /api/v1/tasks/{taskId}/reopen
POST /api/v1/tasks/{taskId}/pause
POST /api/v1/tasks/{taskId}/unpause
DELETE /api/v1/tasks/{taskId}
Authorization: Bearer {accessToken}
```

Para marcar ou desmarcar uma etapa, o índice é baseado em zero e o corpo define o estado final, tornando a operação segura para repetição:

```json
{ "isCompleted": true }
```

Quando todas as etapas somam 100% concluído, a tarefa é concluída automaticamente, como no painel. `/complete` e `/reopen` concluem ou reabrem a tarefa. Para pausar, envie `{ "reason": "Aguardando aprovação", "responsible": "Ana" }`; ambos os campos são opcionais. Uma tarefa concluída não pode ser pausada (`409`). `/unpause` não precisa de corpo. Exclusão retorna `204 No Content`. As operações de conclusão, pausa, retomada, exclusão e checklist também geram histórico.

Rotas de tarefa inexistente retornam `404 task_not_found`; campos ou índices inválidos retornam `400 invalid_input`. Todas as mutações mantêm o banco de tarefas e a lista aberta no painel desktop sincronizados.

### Consultar histórico de tarefas

```http
GET /api/v1/tasks/history?q=release&page=0&pageSize=30
Authorization: Bearer {accessToken}
```

`q` é opcional. `page` começa em zero e `pageSize` varia de 1 a 100 (padrão 30). A resposta contém `{ "items": [...], "totalCount": 1, "page": 0, "pageSize": 30 }`; os itens são ordenados do mais recente para o mais antigo e incluem `id`, `occurredAt`, `commandName`, `beforeValue`, `afterValue`, `source` e `sourceDeviceName`. Este endpoint retorna somente mudanças de tarefas, sem dados de conversão ou relatório.

`source` é `APP` para ações originadas no JMD desktop ou `JMDroid` para ações recebidas pela API. Para `JMDroid`, `sourceDeviceName` identifica o celular que enviou a mudança; isso diferencia aparelhos pareados diferentes. Para `APP`, `sourceDeviceName` é nulo. O histórico visual do JMD também destaca essa origem nos eventos de tarefa.

## Erros

Todas as respostas de erro usam:

```json
{
  "error": {
    "code": "command_unavailable",
    "message": "Este comando exige interação com o computador."
  }
}
```

Códigos esperados: `invalid_pairing_session`, `pairing_expired`, `pairing_denied`, `unauthorized`, `device_revoked`, `command_not_found`, `command_disabled`, `command_unavailable`, `task_not_found`, `task_state_conflict`, `invalid_input`, `attachment_too_large`, `attachment_storage_failed`, `duplicate_request`, `rate_limited`.

- `400`: pedido ou parâmetros inválidos.
- `401`: token ausente/inválido ou sessão de pareamento inválida.
- `403`: aparelho revogado ou comando não permitido.
- `404`: pedido ou comando inexistente.
- `404`: tarefa inexistente (`task_not_found`).
- `409`: estado da tarefa não permite a operação (`task_state_conflict`).
- `409`: requisição repetida com `requestId` já processado.
- `410`: pareamento expirado.
- `422`: parâmetros não passam pelo schema do comando.
- `429`: limite de pedidos excedido.
- `413`: arquivo ou envio de anexos excede o limite.
- `500`: falha ao salvar anexos no computador.

## Regras mínimas de segurança

- Token de pelo menos 256 bits, diferente por aparelho, hash no desktop e armazenamento protegido no Android.
- Aprovação explícita no desktop para cada novo aparelho; QR temporário, aleatório e de uso único.
- HTTPS com pinning da impressão digital do certificado recebido pelo QR; falha fechada se a impressão digital divergir.
- Revogação imediata invalida token e sessão WebSocket daquele aparelho.
- Limite de tentativas de pareamento e de chamadas por aparelho; entradas de usuário com tamanho limitado.
- Somente IDs fixos do catálogo; nunca executar shell ou comandos arbitrários enviados pelo telefone.
- Exibir nome/estado dos aparelhos pareados no desktop e permitir removê-los.

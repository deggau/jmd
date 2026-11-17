# API local do JMD para o JMDroid

**Estado:** implementada no JMD desktop; o cliente Android precisa integrar este contrato.

Este documento define como o JMDroid pode parear com uma instância do JMD na mesma rede local, descobrir e executar comandos, gerenciar tarefas e consultar o histórico de tarefas. Não há servidor central: o JMD no computador executa as ações.

## Premissas

- O computador precisa estar ligado, com o JMD aberto e acessível pela rede local. O app não executa comandos do computador quando ele está desligado.
- A primeira versão aceita conexão na mesma rede local. Não se deve publicar a porta do JMD diretamente na internet.
- A API usa HTTPS por padrão. Opcionalmente, o usuário pode selecionar HTTP para uma rede local confiável; nesse modo, todo o tráfego fica sem criptografia e `certificateSha256` fica vazio. Não exponha a porta fora da rede privada.
- A API só abre o listener enquanto a tela de pareamento por QR estiver aberta ou enquanto existir pelo menos uma sessão WebSocket autenticada. Fechar a tela encerra o listener quando não houver sessão conectada; terminar a última sessão também o encerra.
- Se o listener tiver sido encerrado após uma desconexão, o usuário precisa abrir novamente **Configurações > Android > Conectar** no JMD desktop para permitir uma nova conexão. Reconexão automática com o listener fechado não é possível.

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

O JMDroid mantém uma sessão WebSocket (`wss://` em HTTPS ou `ws://` em HTTP) enquanto estiver conectado. A sessão conta para o ciclo de vida do listener e permite ao desktop exibir o aparelho como conectado. O servidor fecha a sessão e a API ao revogar o aparelho ou encerrar a última conexão quando a tela de pareamento estiver fechada.

Mensagem inicial do servidor:

```json
{
  "type": "session.ready",
  "deviceId": "device-aleatorio",
  "apiVersion": 1
}
```

O servidor envia pings WebSocket de protocolo para manter a sessão e detectar desconexões; clientes WebSocket Android respondem automaticamente no protocolo. O token deve ser enviado apenas em HTTPS/WSS no modo HTTPS. Em HTTP, ele trafega sem criptografia na rede local e nunca deve aparecer em logs.

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

Comandos personalizados aparecem com o mesmo `id` salvo no JMD (prefixo `custom:`). Para habilitar um, edite-o no JMD e marque **Permitir execução pelo JMDroid (Android)**. A permissão é individual por comando e fica desabilitada por padrão. Workflows podem abrir programas, digitar e interagir com janelas deste computador; habilite apenas os comandos que deseja controlar pelo celular. Após habilitar, `inputSchema` descreve as variáveis `{{nome}}` usadas pelas etapas. Envie os valores em `input.variables`; variáveis omitidas ainda podem ser resolvidas pelas variáveis globais cadastradas no JMD.

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

Códigos esperados: `invalid_pairing_session`, `pairing_expired`, `pairing_denied`, `unauthorized`, `device_revoked`, `command_not_found`, `command_disabled`, `command_unavailable`, `task_not_found`, `task_state_conflict`, `invalid_input`, `duplicate_request`, `rate_limited`.

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

## Regras mínimas de segurança

- Token de pelo menos 256 bits, diferente por aparelho, hash no desktop e armazenamento protegido no Android.
- Aprovação explícita no desktop para cada novo aparelho; QR temporário, aleatório e de uso único.
- HTTPS com pinning da impressão digital do certificado recebido pelo QR; falha fechada se a impressão digital divergir.
- Revogação imediata invalida token e sessão WebSocket daquele aparelho.
- Limite de tentativas de pareamento e de chamadas por aparelho; entradas de usuário com tamanho limitado.
- Somente IDs fixos do catálogo; nunca executar shell ou comandos arbitrários enviados pelo telefone.
- Exibir nome/estado dos aparelhos pareados no desktop e permitir removê-los.

# API local do JMD para o JMDroid

**Estado:** implementada no JMD desktop; o cliente Android ainda precisa integrar este contrato.

Este documento define como o JMDroid pode parear com uma instância do JMD na mesma rede local, descobrir os comandos disponíveis, filtrar a lista e solicitar a execução de um comando. Não há servidor central: o JMD no computador executa as ações.

## Premissas

- O computador precisa estar ligado, com o JMD aberto e acessível pela rede local. O app não executa comandos do computador quando ele está desligado.
- A primeira versão aceita conexão na mesma rede local. Não se deve publicar a porta do JMD diretamente na internet.
- A API usa HTTPS. Como o certificado do servidor é local e autoassinado, o JMDroid confia nele somente quando a impressão digital SHA-256 recebida no QR code confere.
- A API só abre o listener enquanto a tela de pareamento por QR estiver aberta ou enquanto existir pelo menos uma sessão WebSocket autenticada. Fechar a tela encerra o listener quando não houver sessão conectada; terminar a última sessão também o encerra.
- Se o listener tiver sido encerrado após uma desconexão, o usuário precisa abrir novamente **Configurações > Android > Conectar** no JMD desktop para permitir uma nova conexão. Reconexão automática com o listener fechado não é possível.

## Pareamento e autenticação

### 1. Abrir o pareamento no desktop

Ao abrir a tela de conexão, o JMD inicia o listener HTTPS em uma porta local disponível e cria uma sessão de pareamento aleatória, válida por 2 minutos e utilizável uma única vez. O QR code contém um objeto JSON neste formato:

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

O `pairingSecret` é temporário e serve apenas para iniciar o pedido. Ele não é o token de acesso do aparelho.

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

O JMDroid mantém uma sessão WebSocket enquanto estiver conectado. A sessão conta para o ciclo de vida do listener e permite ao desktop exibir o aparelho como conectado. O servidor fecha a sessão e a API ao revogar o aparelho ou encerrar a última conexão quando a tela de pareamento estiver fechada.

Mensagem inicial do servidor:

```json
{
  "type": "session.ready",
  "deviceId": "device-aleatorio",
  "apiVersion": 1
}
```

O servidor envia pings WebSocket de protocolo para manter a sessão e detectar desconexões; clientes WebSocket Android respondem automaticamente no protocolo. O token deve ser enviado apenas em HTTPS/WSS e nunca aparecer em logs.

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

Por padrão, só são retornados comandos remotos disponíveis. `includeUnavailable=true` também retorna comandos que dependem de clipboard, seleção/janela em foco, diálogos ou outra interação exclusiva do desktop, com `available: false` e um motivo legível. Comandos desativados nas configurações do JMD também não podem ser executados remotamente.

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

Na primeira versão, operações que dependem do desktop devem ser excluídas do catálogo executável remoto. A API não deve simular teclas, ler o clipboard ou operar janelas em segundo plano sem uma definição explícita de comportamento.

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

Códigos esperados: `invalid_pairing_session`, `pairing_expired`, `pairing_denied`, `unauthorized`, `device_revoked`, `command_not_found`, `command_disabled`, `command_unavailable`, `invalid_input`, `duplicate_request`, `rate_limited`.

- `400`: pedido ou parâmetros inválidos.
- `401`: token ausente/inválido ou sessão de pareamento inválida.
- `403`: aparelho revogado ou comando não permitido.
- `404`: pedido ou comando inexistente.
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

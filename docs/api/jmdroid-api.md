# API local do JMD para o JMDroid

**Estado:** descoberta UDP e pareamento por identidade implementados no JMD desktop. O JMDroid precisa implementar o protocolo Android descrito abaixo. O pareamento por QR com token continua disponível para versões anteriores.

Este documento define como o JMDroid pode parear com uma instância do JMD na mesma rede local, descobrir e executar comandos, gerenciar tarefas e consultar o histórico de tarefas. Não há servidor central: o JMD no computador executa as ações.

## Premissas

- O computador precisa estar ligado, com o JMD aberto e acessível pela rede local. O app não executa comandos do computador quando ele está desligado.
- A conexão aceita aparelhos na mesma rede IPv4 privada. Não se deve publicar a porta do JMD diretamente na internet.
- A API usa HTTPS por padrão. Opcionalmente, o usuário pode selecionar HTTP para uma rede local confiável; nesse modo, todo o tráfego fica sem criptografia e `certificateSha256` fica vazio. Não exponha a porta fora da rede privada.
- O JMD inicia o listener de rede ao abrir o aplicativo. A API usa TCP na porta ativa (47831 por padrão) e responde à descoberta UDP na porta `47832`. O listener permanece ativo enquanto o JMD estiver aberto.
- Para atravessar o Firewall do Windows, o usuário pode configurar regras restritas ao perfil privado, à sub-rede local, à porta TCP ativa e à porta UDP `47832`.
- O JMD usa o mesmo par RSA persistente que identifica conexões entre computadores. A chave privada fica protegida no Windows; o telefone gera sua própria chave no Android Keystore. O `peerId` vincula a chave pública à identidade estável do aparelho.

## Descoberta por broadcast na rede local

O JMDroid deve enviar um datagrama UDP de broadcast por cada interface IPv4 privada conectada, para o broadcast da sub-rede (e opcionalmente `255.255.255.255`) na porta `47832`. A rede pode bloquear broadcast entre VLANs, redes de convidados ou VPNs; nesse caso, use o QR ou conecte os dispositivos à mesma sub-rede.

Pedido (JSON UTF-8, até 512 bytes):

```json
{
  "service": "jmdroid-discovery",
  "apiVersion": 1,
  "nonce": "64 caracteres hexadecimais aleatórios"
}
```

Use um nonce novo de 32 bytes por varredura e aguarde as respostas por aproximadamente 1,5 segundo. O JMD responde por unicast ao endereço de origem do datagrama:

```json
{
  "service": "jmd",
  "apiVersion": 1,
  "nonce": "o mesmo nonce do pedido",
  "computerName": "JMD-DESKTOP-01",
  "port": 47831,
  "scheme": "https",
  "appVersion": "0.1.132.0",
  "peerId": "sha256-da-chave-publica-em-hex-minusculo",
  "publicKey": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n",
  "certificateSha256": "impressão SHA-256 do certificado TLS em hexadecimal",
  "signature": "assinatura RSA-PSS em Base64"
}
```

O JMD assina a string UTF-8, com LF entre campos, nesta ordem: `discovery`, `nonce`, `peerId`, `computerName`, `port`, `scheme`, `certificateSha256`. O JMDroid deve verificar que o nonce corresponde ao pedido, que `peerId` é SHA-256 do PEM público em UTF-8 e que a assinatura RSA-PSS/SHA-256 é válida. Ignore respostas inválidas e deduplique resultados por `peerId`.

Mostre os computadores encontrados com nome, endereço IP de origem, versão e identidade. O usuário toca em **Conectar** para um novo JMD. Para HTTPS, valide o certificado pelo `certificateSha256` recebido; não desative a validação TLS. Antes de parear, confirme também a identidade no endpoint de desafio abaixo. Em uma primeira descoberta, a chave ainda não é conhecida pelo celular: o JMDroid deve guardar o `peerId` como identidade confiável somente após a aprovação do pareamento. Se uma chave já confiável mudar, bloqueie a reconexão automática e peça novo pareamento explícito.

O broadcast permite encontrar dispositivos, mas não autentica por si só um primeiro contato. Para uma conexão nova, o pedido precisa ser aprovado no JMD desktop. Para redes que não permitem broadcast ou como opção de bootstrap fora de banda, continue oferecendo o QR.

### Confirmar a identidade do JMD

Antes de enviar credenciais ou aceitar uma sessão, gere um nonce hexadecimal aleatório de 32 bytes e consulte o endpoint pelo endereço retornado no broadcast:

```http
GET /api/v1/identity/{nonce}
```

Resposta:

```json
{
  "peerId": "sha256-da-chave-publica-em-hex-minusculo",
  "publicKey": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n",
  "signature": "assinatura RSA-PSS em Base64"
}
```

A assinatura cobre `identity\n{nonce}\n{peerId}`. Verifique nonce, `peerId`, chave pública e assinatura. Na reconexão automática, o `peerId` e a chave devem corresponder exatamente ao computador previamente pareado.

## Pareamento e autenticação

### Pareamento e reconexão por identidade (fluxo recomendado)

O JMDroid cria um par RSA no Android Keystore e mantém a chave privada não exportável. Exporte a chave pública como PEM `SubjectPublicKeyInfo`, com linhas LF e Base64 de até 64 caracteres por linha. Calcule `peerId` como SHA-256 dos bytes UTF-8 desse PEM, em hexadecimal minúsculo. Use RSA-PSS com SHA-256 e salt de 32 bytes para as assinaturas.

Depois da descoberta e da confirmação da identidade do JMD, gere `requestId` com 24 bytes aleatórios em hexadecimal minúsculo e `timestamp` em segundos Unix. Assine a string UTF-8:

```text
mobile-connect\n{requestId}\n{deviceName}\n{appVersion}\n{peerId}\n{timestamp}
```

Envie:

```http
POST /api/v1/pairing/identity-requests
Content-Type: application/json
```

```json
{
  "requestId": "48 caracteres hexadecimais aleatórios",
  "deviceName": "POCO X3",
  "appVersion": "0.3.0",
  "peerId": "sha256-da-chave-publica-em-hex-minusculo",
  "publicKey": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n",
  "timestamp": 1795632000,
  "signature": "assinatura RSA-PSS em Base64"
}
```

O JMD confere a assinatura e a origem na rede privada. Para uma chave nova, cria um pedido pendente que aparece na tela **Conectar um aparelho** do computador. O usuário aprova ou nega esse aparelho; o pedido expira em cinco minutos. A resposta pendente é HTTP `202` e contém o estado, a identidade pública do JMD e uma assinatura sobre:

```text
mobile-pairing-state\n{requestId}\n{state}\n{devicePeerId}\n{serverPeerId}\n{tokenHash}
```

`tokenHash` é SHA-256 do token UTF-8 em hexadecimal maiúsculo; fica vazio enquanto não há token. O JMDroid deve validar `serverPeerId`, `serverPublicKey` e a assinatura com a chave conferida no desafio de identidade. Se o estado for `pending`, consulte a cada `pollAfterMs` (1.500 ms) `GET /api/v1/pairing/identity-requests/{requestId}`; cada resposta usa a mesma assinatura por estado.

Exemplo de resposta pendente:

```json
{
  "state": "pending",
  "pollAfterMs": 1500,
  "requestId": "request-id-assinado",
  "devicePeerId": "identidade-publica-do-telefone",
  "deviceId": null,
  "accessToken": null,
  "serverPeerId": "identidade-publica-do-jmd",
  "serverPublicKey": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----\n",
  "certificateSha256": "impressão TLS ou string vazia em HTTP",
  "signature": "assinatura RSA-PSS do estado"
}
```

Quando o estado for `approved`, a resposta também contém `deviceId` e `accessToken`. Verifique a assinatura antes de armazenar o token no armazenamento protegido do Android. O JMD guarda somente o hash do token, o `peerId` e a chave pública do aparelho. O `deviceId` é estável e igual ao `peerId` do JMDroid.

Estados possíveis no novo fluxo: `pending`, `approved`, `denied` e `expired`. Erros do pedido podem incluir `invalid_identity_pairing`, `identity_key_changed`, `same_identity`, `private_network_only` e `rate_limited`.

Em uma reconexão, o JMDroid descobre novamente os computadores e repete o pedido assinado usando sua mesma chave para cada identidade de JMD já salva. Se o `peerId` e a chave pública forem idênticos aos aprovados, o JMD valida a posse da chave, renova o token automaticamente e assina a resposta sem pedir aprovação outra vez. O JMDroid valida novamente a identidade do computador, verifica a assinatura da resposta, grava o token renovado antes de abrir a sessão e não deve executar duas renovações em paralelo para a mesma identidade. Chave diferente, token revogado ou resposta de outro `peerId` não devem ser aceitos automaticamente. Se os dados do app Android forem apagados ou a chave do Keystore for perdida, será necessário aprovar a nova identidade no JMD.

O token retornado continua sendo a credencial das rotas HTTP/WebSocket existentes: envie-o como `Authorization: Bearer {accessToken}`. Isso mantém compatibilidade com o cliente QR atual. Nunca registre token, chave privada ou assinatura de pareamento em logs.

### Usar QR como bootstrap com o protocolo por identidade

O JMDroid atualizado também pode ler o QR para obter `baseUrl` e `certificateSha256` quando o broadcast estiver bloqueado. Depois, deve validar `GET /api/v1/identity/{nonce}` e usar `POST /api/v1/pairing/identity-requests`, igual ao fluxo recomendado. O QR não precisa ser requisito para aparelhos na mesma rede e continua visível no desktop.

### Pareamento legado por QR code

O endpoint `/api/v1/pairing/requests` continua aceito para versões anteriores do JMDroid. Ele usa token sem identidade RSA e serve para compatibilidade; a versão atual do app deve preferir o pareamento por identidade, inclusive quando o endereço veio do QR.

#### 1. Abrir o pareamento no desktop

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

#### 2. Solicitar pareamento pelo JMDroid

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
  "pollAfterMs": 1500,
  "computerName": "JMD-DESKTOP-01"
}
```

`computerName` identifica o computador que está oferecendo o pareamento. O JMDroid deve mostrá-lo e associá-lo à conexão salva, pois um celular pode parear com mais de um computador. Esse campo também aparece nas respostas de consulta abaixo, tanto enquanto o pedido está pendente quanto depois da aprovação. O JMD desktop mostra o nome do aparelho e os botões **Aprovar** e **Negar**. O pedido expira junto com a sessão de pareamento. O QR, por si só, nunca autoriza um aparelho.

#### 3. Consultar a aprovação

```http
GET /api/v1/pairing/requests/{requestId}
X-JMD-Pairing-Secret: segredo-aleatorio-de-uso-unico
```

Resposta após aprovação:

```json
{
  "state": "approved",
  "deviceId": "device-aleatorio",
  "accessToken": "token-opaco-aleatorio-com-pelo-menos-256-bits",
  "computerName": "JMD-DESKTOP-01"
}
```

Enquanto aguarda a decisão, a resposta `202 Accepted` também inclui `computerName`:

```json
{
  "state": "pending",
  "pollAfterMs": 1500,
  "computerName": "JMD-DESKTOP-01"
}
```

`computerName` é o nome do computador no sistema operacional. O JMDroid deve armazená-lo junto de `deviceId` e `accessToken` como identificação da conexão. O JMDroid grava o token no armazenamento protegido pelo Android Keystore e descarta o segredo de pareamento. O desktop persiste somente o hash do token, associado ao aparelho. O token é exclusivo daquele aparelho e revogável no JMD.

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

A transmissão usa a sessão WebSocket autenticada já aberta em `GET /api/v1/session`. **Não abra outro socket para as imagens.** O JMD desktop recebe os quadros e os mostra em uma janela. O visualizador também pode enviar toque, rolagem, arraste e texto digitado ao Android; não há encaminhamento de teclas físicas individuais.

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

### Gestos enviados pelo visualizador do computador

Enquanto a transmissão estiver ativa, o JMD envia os gestos pela **mesma sessão WebSocket autenticada** que recebe os quadros. Um clique esquerdo no quadro vira toque; a roda do mouse vira rolagem. O desktop envia JSON de texto com o mesmo `shareId`:

```json
{
  "type": "screen_share.input",
  "shareId": "id-opaco-da-transmissao",
  "action": "tap",
  "x": 0.5,
  "y": 0.4,
  "frameWidth": 1080,
  "frameHeight": 2400,
  "deltaX": 0,
  "deltaY": 0
}
```

```json
{
  "type": "screen_share.input",
  "shareId": "id-opaco-da-transmissao",
  "action": "scroll",
  "x": 0.5,
  "y": 0.7,
  "frameWidth": 1080,
  "frameHeight": 2400,
  "deltaX": 0,
  "deltaY": 120
}
```

`x` e `y` são coordenadas normalizadas entre `0.0` e `1.0` no quadro JPEG exato. `frameWidth` e `frameHeight` são os pixels desse quadro. Para mapear, calcule `frameX = round(x * (frameWidth - 1))` e `frameY = round(y * (frameHeight - 1))`; depois converta o pixel do quadro para coordenadas físicas da tela usando a região de conteúdo realmente capturada pelo `MediaProjection`. Não some nem remova a altura da barra de status/nav por suposição: se o quadro inclui essas barras, o retângulo capturado começa em `(0,0)`; se o Android removeu algum inset, some o deslocamento real do recorte. Não use dimensões em dp como se fossem pixels. Isso evita erro de posição causado por densidade, orientação ou insets.

`tap` representa um toque simples no ponto. Em `scroll`, `deltaX` e `deltaY` seguem unidades de roda do Windows: `120` representa um passo para cima, `-120` um passo para baixo; o JMD envia atualmente `deltaX: 0`. Converta a rolagem em um gesto de arrastar/swipe na região indicada, com direção e distância coerentes com o delta.

Ao pressionar e mover o botão esquerdo, o JMD envia `action: "drag"` ao soltar. `x`/`y` indicam o início; `endX`/`endY`, o destino (também normalizados no quadro); `durationMilliseconds` informa a duração estimada entre 50 e 5000 ms. Exemplo:

```json
{
  "type": "screen_share.input",
  "shareId": "id-opaco-da-transmissao",
  "action": "drag",
  "x": 0.5,
  "y": 0.03,
  "endX": 0.5,
  "endY": 0.65,
  "durationMilliseconds": 420,
  "frameWidth": 1080,
  "frameHeight": 2400,
  "deltaX": 0,
  "deltaY": 0
}
```

O cliente Android deve executar o arraste como uma única trajetória entre início e destino, usando `AccessibilityService.dispatchGesture` e a duração indicada. Isso permite gestos como abrir a barra de notificações e arrastar um item.

Para digitar no campo atualmente focado no Android, o JMD envia o texto digitado na caixa **Enviar texto** da janela de compartilhamento (Enter ou botão Enviar):

```json
{
  "type": "screen_share.text",
  "shareId": "id-opaco-da-transmissao",
  "text": "Texto digitado no computador"
}
```

O texto tem limite de 4000 caracteres. O cliente deve inseri-lo no campo editável com foco, preservando cursor e seleção quando possível, e responder pela mesma sessão com `screen_share.text_result` (`success: true/false`, opcionalmente `message`). Se não conseguir executar gesto ou inserir texto, responda `screen_share.input_error` com `shareId` e `message`. A janela do JMD mostra a resposta como aviso temporário por 10 segundos. Os eventos são limitados a no máximo 50 por segundo pelo JMD.

Para reproduzir gestos em outras aplicações, o cliente Android precisa de um `AccessibilityService` habilitado explicitamente pelo usuário e pode usar `dispatchGesture` (API 24+). Declare os metadados e capacidades de gesto exigidos pelo Android. A captura por `MediaProjection` **não** concede permissão para controlar a tela. Se o serviço não estiver habilitado, o Android deve ignorar o gesto ou responder pela mesma sessão com:

```json
{
  "type": "screen_share.input_error",
  "shareId": "id-opaco-da-transmissao",
  "message": "Acessibilidade para controle remoto não está habilitada."
}
```

O JMD mostra essa mensagem na janela de transmissão. Aceite gestos somente quando `shareId` for a transmissão ativa e descarte coordenadas fora do intervalo, controles de sessões antigas e eventos recebidos após a parada. O usuário precisa habilitar a acessibilidade nas configurações do Android; não habilite nem contorne essa confirmação programaticamente.

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

## Ver a tela do computador no Android

Neste fluxo a direção da imagem é inversa: o JMD captura a **tela principal do computador**, reduz os quadros ao tamanho de visualização informado pelo Android e envia JPEGs ao celular. O JMDroid não precisa usar `MediaProjection`; ele precisa abrir uma tela/visualizador e consumir mensagens binárias do WebSocket autenticado.

O usuário pode iniciar a visualização nos dois lados:

- **No computador:** escolha **Ver tela do computador no celular** na paleta, use o atalho padrão `Ctrl+Alt+Shift+D` ou diga uma das frases de voz configuradas para o comando (por padrão: “ver tela do computador”, “ver tela do desktop” e “compartilhar tela do computador”). O atalho e as frases podem ser alterados em **Configurações > Atalhos**. Se houver mais de um aparelho conectado, o JMD pede para escolher o celular. Executar o comando de novo para o mesmo aparelho encerra o compartilhamento ativo.
- **No celular:** selecione `desktopScreenShare` no catálogo remoto. A ação chama o endpoint padrão de execução e o JMD envia o pedido de início pelo WebSocket.

### Iniciar pelo catálogo remoto

`GET /api/v1/commands` inclui `desktopScreenShare` com o nome **Ver tela do computador no celular**, `available: true` quando o comando não está desativado e schema de entrada vazio. Execute:

```http
POST /api/v1/commands/desktopScreenShare/execute
Authorization: Bearer {accessToken}
Content-Type: application/json
```

```json
{
  "requestId": "uuid-unico-por-acao",
  "input": {}
}
```

A resposta `status: "succeeded"` confirma que o pedido foi enviado à sessão conectada do Android; ainda não confirma que o visualizador está pronto ou que um quadro chegou. Use a sessão WebSocket para receber o controle de início e os quadros. O JMD mantém no máximo uma transmissão ativa por aparelho. Repetir o mesmo `requestId` devolve o resultado anterior; uma nova ação precisa de um ID novo.

### Pedido e resolução de destino

Tanto o atalho do computador quanto a ação do catálogo enviam a mesma mensagem de texto pela sessão:

```json
{
  "type": "desktop_screen_share.start",
  "shareId": "id-opaco-da-transmissao",
  "maxFps": 12,
  "maxDimension": 1440,
  "maxFrameBytes": 524288,
  "format": "jpeg",
  "quality": 55
}
```

O JMDroid deve abrir o visualizador e responder pela **mesma sessão WebSocket** com uma mensagem de texto `desktop_screen_share.ready`. `width` e `height` indicam a resolução em pixels que o visualizador consegue mostrar; o JMD reduz e mantém a proporção dentro desse tamanho e de `maxDimension`:

```json
{
  "type": "desktop_screen_share.ready",
  "shareId": "id-opaco-da-transmissao",
  "width": 1080,
  "height": 2400
}
```

Envie dimensões positivas correspondentes ao espaço disponível no visualizador, após considerar orientação e barras do sistema. O JMD limita o maior lado dos quadros da tela inteira a 1440 px. `shareId` deve ser ecoado sem alterações. Se receber novamente o mesmo `shareId`, reutilize o visualizador atual e reenvie `ready`; não crie telas ou sessões duplicadas. O Android pode enviar `ready` novamente ao mudar a orientação/resolução para o JMD ajustar a captura.

`maxFps` é o teto configurado no JMD para ambas as direções (12–60 FPS; padrão 12). `maxFrameBytes` é o máximo por mensagem binária (512 KiB). `quality` é a qualidade JPEG solicitada; o codificador pode reduzir a qualidade e, se necessário, a resolução até caber no limite de tamanho.

### Pedir uma área ampliada da tela

Por padrão, o JMD transmite a tela principal inteira, reduzida para caber nas dimensões de `ready`. Quando o usuário ampliar ou mover a imagem no Android, o app pode pedir ao JMD que capture somente a região visível. Envie pela mesma sessão WebSocket uma mensagem de texto:

```json
{
  "type": "desktop_screen_share.viewport",
  "shareId": "id-opaco-da-transmissao",
  "x": 0.25,
  "y": 0.2,
  "width": 0.5,
  "height": 0.6
}
```

`x`, `y`, `width` e `height` são frações entre `0` e `1`, relativas à tela principal inteira, com origem no canto superior esquerdo. O retângulo precisa permanecer dentro da tela (`x + width <= 1`, `y + height <= 1`) e ter pelo menos `0.02` de largura e altura. Por exemplo, `x: 0.25`, `y: 0.2`, `width: 0.5`, `height: 0.6` pede a área central da tela. Envie um novo retângulo ao mover ou alterar o zoom. Para voltar à tela inteira, envie `x: 0`, `y: 0`, `width: 1`, `height: 1`.

O JMD aplica a região no próximo quadro, captura seus pixels na resolução física da tela e transmite a imagem recortada sem faixas laterais. Para ampliar, isso recupera detalhe que foi descartado quando a tela inteira foi reduzida. O quadro do recorte pode chegar a 2400 px no maior lado, mas não excede a resolução física da região; o limite de 512 KiB continua valendo e o codificador reduz a qualidade/resolução se necessário. O Android deve exibir cada quadro recebido ocupando o retângulo ampliado e manter sua proporção. O `shareId` precisa corresponder à transmissão ativa; regiões inválidas são ignoradas. A captura continua limitada à tela principal.

### Receber quadros

Depois do `desktop_screen_share.ready`, o JMD envia cada imagem como uma mensagem WebSocket **binária completa** contendo somente os bytes de um JPEG. O visualizador do Android deve ler mensagens de texto e binárias no mesmo loop do WebSocket: mensagens de texto são controles JSON; cada mensagem binária é um quadro JPEG independente. Não aguarde um envelope JSON em torno da imagem nem abra outro WebSocket.

```text
WebSocket TEXT:   {"type":"desktop_screen_share.start", ...}
WebSocket TEXT:   {"type":"desktop_screen_share.ready", ...}  (Android → JMD)
WebSocket BINARY: FF D8 ... FF D9   (JMD → Android; um JPEG)
WebSocket BINARY: FF D8 ... FF D9   (próximo quadro)
```

O JMD ajusta a tela principal para caber no espaço informado pelo Android, sem ampliar a captura e sem distorcer sua proporção (em uma tela de celular retrato, uma tela desktop paisagem terá faixas vazias acima/abaixo). O maior lado dos quadros da tela inteira fica limitado a 1440 px; quadros de uma área ampliada seguem o limite de 2400 px descrito acima e a resolução física da área. O JMD tenta manter cada JPEG abaixo de 512 KiB e aplica o limite de FPS. O servidor pode enviar menos quadros por segundo para respeitar o teto ou acompanhar o desempenho. Exiba sempre o quadro mais recente e descarte imagens atrasadas para não formar fila. Preserve a proporção; não estique a imagem para preencher a tela.

### Encerrar e falhas

Para encerrar, o JMD envia `desktop_screen_share.stop` quando o usuário executa novamente o atalho/comando no computador. O Android também pode encerrar pelo botão **Parar** no visualizador. Em ambos os casos, a mensagem de parada vai pelo WebSocket:

```json
{ "type": "desktop_screen_share.stop", "shareId": "id-opaco-da-transmissao" }
```

Quando o Android enviar `desktop_screen_share.stop`, o JMD para imediatamente a captura e responde `desktop_screen_share.stopped`. Quando o JMD enviar `stop`, o Android deve fechar o visualizador, descartar o quadro atual e responder uma vez com:

```json
{ "type": "desktop_screen_share.stopped", "shareId": "id-opaco-da-transmissao" }
```

Se o JMD não conseguir capturar ou codificar a tela, envia `desktop_screen_share.error` com `shareId` e uma mensagem curta:

```json
{ "type": "desktop_screen_share.error", "shareId": "id-opaco-da-transmissao", "message": "Não foi possível capturar a tela principal." }
```

Ignore controles com outro `shareId`. Ao desconectar o WebSocket, feche o visualizador e descarte os quadros. A captura é somente da tela principal; não transmite áudio e não recebe toque, teclado nem comandos para controlar o computador.

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
      "icon": null,
      "voicePhrases": [],
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

`icon` é `null` para comandos nativos sem ícone configurado. Em workflows, pode conter uma imagem PNG como data URL (`data:image/png;base64,...`), preparada em 64 × 64 pixels a partir do ícone do programa aberto ou do favicon do site. O Android pode decodificar a parte Base64 depois da vírgula e usar a imagem na lista. `voicePhrases` contém as frases configuradas para aquele workflow, ou uma lista vazia quando não há frases.

Por padrão, são retornados os comandos disponíveis e os comandos personalizados, mesmo quando a execução remota ainda está desativada. `includeUnavailable=true` também retorna os demais comandos locais que dependem de clipboard, seleção/janela em foco, diálogos ou outra interação exclusiva do desktop. Itens indisponíveis incluem `available: false` e um motivo legível. Comandos desativados nas configurações do JMD não podem ser executados remotamente.

Comandos personalizados aparecem com o mesmo `id` salvo no JMD (prefixo `custom:`) e permitem execução pelo JMDroid por padrão. Para bloquear um workflow, edite-o no JMD e marque **Bloquear execução pelo JMDroid (Android)**. Workflows podem abrir programas, digitar, falar texto nos alto-falantes do computador e interagir com janelas deste computador; bloqueie individualmente os comandos que não deseja controlar pelo celular. A etapa **Falar texto em voz alta** usa a síntese local do Windows e não precisa de rede. Nela, `{{hora}}` e `{{data}}` são preenchidas com os valores atuais quando o workflow executa. `inputSchema` descreve as variáveis `{{nome}}` usadas pelas etapas. Envie os valores em `input.variables`; variáveis omitidas ainda podem ser resolvidas pelas variáveis globais cadastradas no JMD. O catálogo retorna também as frases do Jey em `voicePhrases`, para exibição no Android.

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

Workflows personalizados habilitados são executados no computador como se fossem acionados no JMD. Os comandos nativos `todoCreate`, `todoPanel`, `todoPanelClose`, `about`, `lock`, `suspend` e `hibernate` também ficam disponíveis, desde que não estejam desativados nas configurações do JMD. Eles abrem a criação de tarefa, abrem/fecham o painel, abrem a tela Sobre ou executam a ação de energia no computador conectado. Envie `"input": {}` para executá-los. Outros comandos que dependem de interação exclusiva com o desktop continuam indisponíveis.

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

Códigos esperados: `invalid_pairing_session`, `pairing_expired`, `pairing_denied`, `invalid_identity_challenge`, `invalid_identity_pairing`, `identity_key_changed`, `same_identity`, `private_network_only`, `unauthorized`, `device_revoked`, `command_not_found`, `command_disabled`, `command_unavailable`, `task_not_found`, `task_state_conflict`, `invalid_input`, `attachment_too_large`, `attachment_storage_failed`, `duplicate_request`, `rate_limited`.

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

- Chave privada RSA do JMD protegida pelo Windows e chave privada do aparelho não exportável pelo Android Keystore; o desktop guarda a chave pública aprovada.
- Aprovação explícita no desktop para cada identidade nova. A mesma chave aprovada pode renovar o token sem novo pedido; chave diferente exige aprovação.
- Tokens com pelo menos 256 bits, renovados no pareamento por identidade, diferentes por aparelho, guardados somente como hash no desktop e em armazenamento protegido no Android.
- HTTPS com pinning do certificado descoberto e validação de identidade RSA assinada. Nunca aceite automaticamente outro `peerId` nem ignore divergência de chave ou certificado.
- As respostas UDP são limitadas à rede privada, incluem nonce aleatório e assinatura; não trate broadcast como autenticação do primeiro contato. Mantenha aprovação visível no JMD desktop e ofereça QR como canal alternativo de bootstrap.
- Revogação imediata invalida token e sessão WebSocket daquele aparelho.
- Limite de tentativas de pareamento e de chamadas por aparelho; entradas de usuário com tamanho limitado.
- Somente IDs fixos do catálogo; nunca executar shell ou comandos arbitrários enviados pelo telefone.
- Exibir nome/estado dos aparelhos pareados no desktop e permitir removê-los.

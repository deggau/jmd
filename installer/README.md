# Instalação Windows

O instalador é gerado com NSIS e instala somente para o usuário atual. Não exige privilégios administrativos. Cria atalhos no menu Iniciar e uma entrada em Aplicativos instalados. A configuração do usuário fica em `%LOCALAPPDATA%\JMD` e é mantida ao desinstalar.

## Pré-requisitos de build

- Windows 10 ou posterior.
- .NET 10 SDK.
- NSIS instalado, com `makensis.exe` disponível no `PATH`.
- PowerShell 7 ou Windows PowerShell 5.1.

## Gerar instalador

Na raiz do repositório:

```powershell
./installer/Build-Installer.ps1
```

O script publica a aplicação como self-contained para `win-x64`, inclui os modelos inglês e português do Vosk e os mecanismos locais Whisper e llama.cpp. Whisper Tiny, Whisper Base, Gemma E2B e modelos Sherpa ONNX podem ser baixados separadamente nas configurações do Jey; o Gemma e seu codificador de áudio ocupam cerca de 3,2 GiB. O instalador gera `artifacts/installer/JMD-0.1.93-win-x64-setup.exe`. O inglês detecta “Jey”; o português transcreve apenas a fala posterior. Para outra versão:

```powershell
./installer/Build-Installer.ps1 -Runtime win-x64 -Version 0.1.93
```

## Instalar a partir de uma release pública

Com o repositório público e uma release disponível, abra o PowerShell e execute este comando para instalar a versão x64 mais recente:

```powershell
irm https://raw.githubusercontent.com/deggau/jmd/main/installer/install.ps1 | iex
```

O comando baixa `installer/install.ps1` da branch principal. Esse script baixa o instalador da release mais recente, confere o checksum SHA-256 e inicia o assistente. Ao concluir, a opção pré-selecionada inicia o JMD na tela de comandos; o usuário pode desmarcá-la na tela final. O instalador fecha qualquer instância aberta do JMD antes de substituir os arquivos. Para publicar uma versão, envie uma tag como `v0.1.93`; a GitHub Action compila o instalador no Windows e publica o instalador e o checksum na release. O repositório precisa estar público para o comando remoto funcionar sem autenticação.

O instalador também adiciona a pasta do JMD ao `PATH` do usuário e instala o comando `jmd`. Em um novo terminal:

```powershell
jmd -ls
jmd UpperCase
jmd -help
jmd -search tarefa
jmd -run 2 tarefa
jmd -ls --verbose
```

`jmd <texto>` usa a busca da paleta, informa o comando escolhido e aciona o primeiro resultado. `-search <texto>` mostra resultados numerados sem executar; `-run <n> [texto]` executa o resultado numerado da busca ou, sem texto, o item numerado da lista alfabética. `-ls` lista nomes e atalhos; `-ls --verbose` inclui descrições e estado ativado/desativado. `-help` mostra a sintaxe. Ao remover o JMD, o instalador remove a entrada de `PATH` que adicionou.

O instalador configura autocomplete com Tab automaticamente nos perfis do PowerShell 7 e do Windows PowerShell 5.1. Abra um novo terminal para usar. `jmd -install-completion` reaplica a configuração e `jmd -uninstall-completion` remove. Ao desinstalar o JMD, a configuração também é removida. Workflows personalizados aparecem na lista, nos resultados e no autocomplete; digitar o nome exato de um workflow executa esse workflow diretamente.

Os códigos de saída são `0` para sucesso, `1` quando a busca não encontra o comando ou índice pedido e `2` para erro de uso ou falha de execução.

Pelo menu **Sobre** do JMD, a atualização automática baixa o instalador `.exe`, valida o checksum SHA-256 publicado e inicia o executável diretamente pelo aplicativo, sem executar script PowerShell.

Nas versões que suportam atualização por linha de comando, abra o PowerShell e execute para iniciar a verificação e instalar automaticamente a versão mais recente:

```powershell
Start-Process "$env:LOCALAPPDATA\Programs\JMD\JMD.App.exe" -ArgumentList '--update'
```

O argumento `--update` também é encaminhado para uma instância do JMD que já esteja aberta.

A publicação inclui o runtime .NET, então o usuário final não precisa instalar o .NET separadamente. A compilação do instalador e a instalação/desinstalação devem ser verificadas em Windows; o script do NSIS não pode ser executado neste ambiente Linux.

## Instalar e remover

Execute o instalador e siga as páginas de boas-vindas, pasta e progresso. Para remover, use **Configurações > Aplicativos > Aplicativos instalados** ou o atalho **Uninstall JMD** no menu Iniciar. A remoção apaga os arquivos instalados e atalhos, preservando as preferências do usuário.

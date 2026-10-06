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

O script publica a aplicação como self-contained para `win-x64` e gera `artifacts/installer/JMD-0.1.1-win-x64-setup.exe`. Para ARM64 ou outra versão:

```powershell
./installer/Build-Installer.ps1 -Runtime win-arm64 -Version 0.1.1
```

## Instalar a partir de uma release pública

Com o repositório público e uma release disponível, abra o PowerShell e execute este comando para instalar a versão x64 mais recente:

```powershell
irm https://raw.githubusercontent.com/deggau/jmd/main/installer/install.ps1 | iex
```

O comando baixa `installer/install.ps1` da branch principal. Esse script baixa o instalador da release mais recente, confere o checksum SHA-256 e inicia o assistente. Para publicar uma versão, envie uma tag como `v0.1.1`; a GitHub Action compila o instalador no Windows e publica o instalador e o checksum na release. O repositório precisa estar público para o comando remoto funcionar sem autenticação.

A publicação inclui o runtime .NET, então o usuário final não precisa instalar o .NET separadamente. A compilação do instalador e a instalação/desinstalação devem ser verificadas em Windows; o script do NSIS não pode ser executado neste ambiente Linux.

## Instalar e remover

Execute o instalador e siga as páginas de boas-vindas, pasta e progresso. Para remover, use **Configurações > Aplicativos > Aplicativos instalados** ou o atalho **Uninstall JMD** no menu Iniciar. A remoção apaga os arquivos instalados e atalhos, preservando as preferências do usuário.

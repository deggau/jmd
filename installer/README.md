# Instalação Windows

O instalador é gerado com NSIS e instala somente para o usuário atual. Não exige privilégios administrativos. Cria atalhos no menu Iniciar e uma entrada em Aplicativos instalados. A configuração do usuário fica em `%LOCALAPPDATA%\DevToolbox` e é mantida ao desinstalar.

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

O script publica a aplicação como self-contained para `win-x64` e gera `artifacts/installer/DevToolbox-0.1.0-win-x64-setup.exe`. Para ARM64 ou outra versão:

```powershell
./installer/Build-Installer.ps1 -Runtime win-arm64 -Version 0.2.0
```

A publicação inclui o runtime .NET, então o usuário final não precisa instalar o .NET separadamente. A compilação do instalador e a instalação/desinstalação devem ser verificadas em Windows; o script do NSIS não pode ser executado neste ambiente Linux.

## Instalar e remover

Execute o instalador e siga as páginas de boas-vindas, pasta e progresso. Para remover, use **Configurações > Aplicativos > Aplicativos instalados** ou o atalho **Uninstall DevToolbox** no menu Iniciar. A remoção apaga os arquivos instalados e atalhos, preservando as preferências do usuário.

# DevToolbox

Suite local de ferramentas para acelerar tarefas comuns de desenvolvimento, acionadas principalmente por atalhos globais no Windows.

## Objetivo inicial

Permitir que o usuário transforme o conteúdo da área de transferência sem trocar de janela. Exemplo: após copiar um texto, pressionar `Ctrl+Alt+I` executa a transformação SQL `IN` e coloca o resultado de volta no clipboard, pronto para colar.

## Documentação

- [Visão do produto](docs/visao/README.md)
- [Requisitos e limites](docs/requisitos/README.md)
- [Arquitetura inicial](docs/arquitetura/README.md)
- [Plano de entrega](docs/planejamento/README.md)
- [Casos de uso](docs/use-cases/README.md)
- [Decisões técnicas](docs/decisoes/README.md)

## Stack

- C# com .NET 10.
- WPF para a paleta e a tela de atalhos.
- APIs Win32 isoladas no projeto `DevToolbox.Windows`.
- NSIS para o instalador Windows por usuário.

## Estrutura

- `src/DevToolbox.App`: aplicação WPF residente e interface.
- `src/DevToolbox.Core`: contratos e tipos compartilhados.
- `src/DevToolbox.Tools`: transformações independentes da plataforma.
- `src/DevToolbox.Windows`: atalhos globais, clipboard e automação de teclado.

## Executar no Windows

Requer o SDK .NET 10 instalado.

```powershell
dotnet build .\DevToolbox.sln
dotnet run --project .\src\DevToolbox.App\DevToolbox.App.csproj
```

A aplicação inicia minimizada na área de notificação. `Win+J` abre a paleta, `Ctrl+Alt+I` converte o texto do clipboard e `Ctrl+Shift+I` transforma e substitui a seleção. Os atalhos podem ser reconfigurados pela paleta; comandos podem ser desativados sem apagar sua configuração.

## Criar instalador Windows

Com o .NET 10 SDK e o NSIS instalados e `makensis.exe` no `PATH`, execute `./installer/Build-Installer.ps1`. O procedimento e os parâmetros estão descritos em [installer/README.md](installer/README.md).

## Status

MVP inicial em implementação. O protótipo cobre conversão SQL `IN`, paleta WPF, armazenamento local, registro de atalhos globais e operações sobre o clipboard/seleção. O funcionamento real precisa ser validado no Windows nas aplicações alvo, especialmente o atalho `Win+J` e a recuperação da seleção após falhas.

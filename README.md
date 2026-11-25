# JMD

O JMD é uma caixa de ferramentas residente para Windows. A paleta e os atalhos globais permitem executar ações sobre texto e automatizar tarefas sem trocar de janela. O padrão é manter configurações e histórico localmente; recursos de rede e serviços externos são opcionais e precisam ser configurados.

## O que o JMD oferece

- **Paleta e atalhos globais:** pesquisar e executar comandos; atalhos podem ser alterados e comandos desativados.
- **Ferramentas de texto:** converter listas para SQL `IN`, alterar maiúsculas/minúsculas e formatar ou compactar JSON e XML.
- **Snippets e rascunhos:** salvar textos pelo título, inserir conteúdo reutilizável e guardar/restaurar rascunhos.
- **Workflows personalizados:** encadear ações configuradas, com variáveis e parâmetros definidos pelo usuário.
- **Tarefas:** criar e acompanhar tarefas, checklists, lembretes e referências a arquivos ou URLs.
- **Voz:** acionar comandos e transcrever fala com os mecanismos disponíveis nas configurações do Jey.
- **Android e rede local:** descobrir computadores por broadcast, aprovar identidades por chave pública, reconectar aparelhos conhecidos, executar comandos autorizados, consultar tarefas e compartilhar a tela. QR segue disponível como alternativa.
- **Histórico e energia:** consultar ações recentes e pedir ao Windows para manter a tela ativa durante uma atividade.

Os recursos disponíveis dependem da configuração e dos dispositivos conectados. A integração Android usa a rede local; não exponha a porta do JMD diretamente à internet. Para o contrato de pareamento, autenticação e compartilhamento, consulte [a documentação da API](docs/api/jmdroid-api.md).

## Documentação

- [Visão do produto](docs/visao/README.md)
- [Requisitos e limites](docs/requisitos/README.md)
- [Arquitetura](docs/arquitetura/README.md)
- [Planejamento](docs/planejamento/README.md)
- [Decisões técnicas](docs/decisoes/README.md)
- [Riscos conhecidos](docs/riscos/README.md)
- [Casos de uso](docs/use-cases/README.md)
- [Testes e validação no Windows](docs/testes/README.md)
- [API local para Android](docs/api/jmdroid-api.md)
- [Instalador](installer/README.md)

## Stack e estrutura

- C# com .NET 10 e interface WPF.
- APIs Win32 isoladas no projeto `JMD.Windows`.
- NSIS para o instalador Windows por usuário.
- `src/JMD.App`: aplicação residente e interface.
- `src/JMD.Core`: políticas, modelos e serviços compartilhados.
- `src/JMD.Tools`: transformações de texto.
- `src/JMD.Windows`: atalhos globais, clipboard e automação de entrada.

## Executar no Windows

Com o SDK .NET 10 instalado:

```powershell
dotnet build .\JMD.sln
dotnet run --project .\src\JMD.App\JMD.App.csproj
```

Por padrão, `Alt+J` abre a paleta, `Ctrl+Shift+I` formata o clipboard para SQL `IN`, `Ctrl+Alt+I` transforma uma seleção, `Ctrl+Shift+Y` procura snippets e `Ctrl+Alt+Shift+Y` salva uma seleção como snippet. Os atalhos são configuráveis e podem estar indisponíveis se outra aplicação já os tiver registrado.

O comando de seleção copia o texto antes de transformar. Se a transformação falhar, a seleção continua no lugar; se a colagem final falhar, o resultado fica no clipboard para colagem manual. Aplicativos elevados, sessões remotas e campos protegidos podem bloquear atalhos ou entrada sintetizada. A compatibilidade deve ser confirmada por aplicativo; veja a [matriz de validação no Windows](docs/testes/validacao-windows.md).

Para classificar workflows antigos salvos como `Open`, execute `./tools/Organize-Workflows.ps1` no PowerShell. A execução padrão só mostra uma prévia. Para aplicar a conversão ao `settings.json` do usuário, use `./tools/Organize-Workflows.ps1 -Apply`; o script cria um backup ao lado do arquivo antes de salvar. Um arquivo alternativo pode ser selecionado com `-Path`.

## Instalar

O instalador publicado inclui o runtime do .NET. Com o repositório público, abra o PowerShell e execute:

```powershell
irm https://raw.githubusercontent.com/deggau/jmd/main/installer/install.ps1 | iex
```

O script baixa o instalador x64 da release mais recente, verifica o SHA-256 e abre o assistente. Para compilar um instalador localmente, consulte [installer/README.md](installer/README.md).

## Limites conhecidos

- Atalhos globais dependem de disponibilidade no Windows e podem conflitar com outros programas.
- A substituição de texto depende de o aplicativo aceitar `Ctrl+C`/`Ctrl+V` sintetizados e manter a janela de origem ativa.
- O compartilhamento Android depende de pareamento, conectividade na rede local e consentimento de captura no aparelho.
- Validação de interação com Win32, clipboard real e aplicativos de terceiros precisa ser feita em uma sessão Windows; os testes automatizados cobrem principalmente regras e lógica isoladas.

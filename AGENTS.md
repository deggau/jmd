# Instruções para agentes do JMD

## Validar mudanças

- Execute a suíte unitária com `dotnet test JMD.sln --no-restore -m:1`.
- Compile toda a solução com `dotnet build JMD.sln --no-restore -m:1`.
- Confira `git diff --check` antes de criar o commit.
- A compilação local valida o XAML e o código, mas testes de interação com Win32, clipboard real e janelas de outros aplicativos precisam de validação no Windows.

## Atualizar versão e publicar instalador

Cada release do instalador precisa de uma versão nova. A versão da aplicação, o nome do instalador e a tag Git devem corresponder. Antes de publicar:

1. Determine a próxima versão SemVer e atualize todas estas referências:
   - `src/JMD.App/JMD.App.csproj`: propriedade `<Version>`.
   - `installer/Build-Installer.ps1`: valor padrão do parâmetro `$Version`.
   - `installer/JMD.nsi`: valor alternativo de `APP_VERSION`.
   - `installer/README.md`: nome de exemplo do instalador, exemplo do comando de build e exemplo da tag.
2. Procure referências de release antigas nos mesmos arquivos com `rg -n '0\.1\.N' ...` e confira que não ficou uma versão antiga onde deveria estar a nova. Não altere exemplos de versões antigas em testes que estejam verificando comparação ou migração.
3. Execute todos os testes, a compilação completa e `git diff --check`. Corrija qualquer falha antes de publicar.
4. Inclua as mudanças pretendidas em um commit e crie uma tag nova no formato `vX.Y.Z`, apontando para esse commit. Não reutilize nem force uma tag de release já publicada.
5. Envie o commit e a tag: `git push origin main vX.Y.Z`.
6. O workflow `.github/workflows/release.yml` é disparado pelo push de qualquer tag `v*`. Localize a execução com `gh run list --workflow "Build and publish Windows installer" --limit 5` e acompanhe-a com `gh run watch ID --exit-status`. Aguarde o estado final: só considere a geração concluída se o resultado for sucesso.
7. Confirme a release com `gh release view vX.Y.Z --json tagName,url,assets`. Verifique que existem tanto `JMD-X.Y.Z-win-x64-setup.exe` quanto o arquivo `.sha256` correspondente e que ambos foram carregados.
8. Confirme que `main` e `origin/main` estão no commit publicado, que a tag aponta para ele e que `git status --short --branch` não mostra alterações pendentes.
9. Na resposta final, informe a versão, o commit, o resultado da pipeline e os links diretos para o instalador e a página da release.

O script `installer/install.ps1` busca a release mais recente, confere o SHA-256 e inicia o instalador. A pipeline do GitHub Actions compila em Windows e publica o instalador e seu checksum; criar apenas uma tag local não executa esse fluxo.

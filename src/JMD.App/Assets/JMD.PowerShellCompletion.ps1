$completionScript = {
    param($wordToComplete, $commandAst, $cursorPosition)

    if ($wordToComplete.StartsWith('-')) {
        $options = @('-help', '-ls', '-search', '-run', '-install-completion', '-uninstall-completion')
        $options | Where-Object { $_.StartsWith($wordToComplete, [System.StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
        return
    }

    try {
        & jmd -complete $wordToComplete 2>$null | ForEach-Object {
            $name = $_.Trim()
            if ($name) {
                $insertText = if ($name -match '\s') { "'" + $name.Replace("'", "''") + "'" } else { $name }
                [System.Management.Automation.CompletionResult]::new($insertText, $name, 'ParameterValue', $name)
            }
        }
    }
    catch { }
}

$completerParameters = (Get-Command Register-ArgumentCompleter).Parameters
if ($completerParameters.ContainsKey('Native')) {
    Register-ArgumentCompleter -CommandName jmd -Native -ScriptBlock $completionScript
}
else {
    Register-ArgumentCompleter -CommandName jmd -ScriptBlock $completionScript
}

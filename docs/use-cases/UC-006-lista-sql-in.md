# UC-006 — Converter lista em valores para SQL `IN`

## Ator

Desenvolvedor preparando uma lista de valores para uma consulta SQL.

## Entrada e saída

- Entrada: texto no clipboard ou texto selecionado numa aplicação.
- Atalho padrão do modo clipboard: `Ctrl+Alt+I`.
- Atalho padrão do modo substituir seleção: `Ctrl+Shift+I`.
- Saída: cada valor envolvido em apóstrofos simples e separado dos demais por vírgula, sem vírgula final.

Exemplos:

```text
Entrada:  abc,def,jeg
Saída:    'abc','def','jeg'

Entrada:  1233456;asdasdas;asdsadas
Saída:    '1233456','asdasdas','asdsadas'
```

Mesmo valores numéricos são envolvidos em apóstrofos, conforme o formato solicitado.

## Delimitadores aceitos

- Vírgula: `,`
- Pipe: `|`
- Ponto e vírgula: `;`
- Quebra de linha CRLF (`\r\n`), LF (`\n`) ou CR (`\r`)

CRLF conta como um único separador, não como dois. Espaços e tabulações em volta de cada item são removidos.

## Algoritmo proposto

Contrato interno sugerido: uma função pura recebe o texto de entrada e um delimitador opcional escolhido pelo usuário; devolve o texto SQL formatado ou um erro de validação. Ela não lê nem grava o clipboard. Isso permite reutilizar a mesma transformação nos modos clipboard e seleção.

1. Ler o texto e rejeitar clipboard vazio ou apenas espaços.
2. Normalizar CRLF e CR para LF para a análise de linhas.
3. Contar ocorrências de cada delimitador candidato.
4. Se nenhum delimitador aparecer, tratar o texto completo como um único valor.
5. Se um delimitador tiver contagem maior que os demais, separá-lo como delimitador da entrada.
6. Se houver empate entre delimitadores presentes, não adivinhar: informar ambiguidade e permitir selecionar um delimitador para esta execução ou configurar a preferência.
7. Remover espaços/tabulações no início e fim de cada item.
8. Ignorar separadores excedentes no início/fim; se houver item vazio entre separadores consecutivos, informar entrada inválida em vez de descartar conteúdo silenciosamente.
9. Escapar cada apóstrofo interno duplicando-o, como `O'Brien` → `O''Brien`.
10. Envolver cada item com `'` e juntar itens com `,`, sem adicionar vírgula no final.
11. Só substituir o clipboard (e, no modo seleção, colar) quando toda a conversão for bem-sucedida.

## Fora do primeiro escopo

- CSV com campos entre aspas que podem conter vírgulas, pipes ou quebras de linha.
- Valores SQL sem aspas, formatos específicos de dialeto ou binding de parâmetros.
- Inferir se um item é texto, número, data ou expressão SQL.

## Alternativas e falhas

- **A1 — Vários delimitadores empatados:** pedir escolha/configuração do delimitador; não modificar a entrada.
- **A2 — Campo vazio interno, por exemplo `abc,,def`:** informar onde está o valor vazio e cancelar.
- **A3 — Apóstrofo no valor:** escapar duplicando o apóstrofo interno antes de cercar o valor.
- **A4 — Erro de transformação:** clipboard e seleção não devem receber saída parcial.

## Critérios de aceite

- Os exemplos acima produzem exatamente as saídas indicadas.
- Entradas delimitadas por cada um dos formatos suportados são convertidas.
- CRLF é interpretado como um único separador.
- Valores são aparados e apóstrofos internos escapados.
- Saída nunca termina em vírgula.
- Entrada ambígua ou com valor vazio interno falha sem substituir o conteúdo original.

function Test-ReusProtectedStructure([string]$Before,[string]$After,[string]$Key='') {
    $regex=[regex]'\[[^\]]*\]|\{\{[\s\S]*?\}\}|\{[^{}]*\}|<[^>]*>'
    $movable='^\{\d+(?:,-?\d+)?(?::[^{}]+)?\}$'
    $beforeTokens=@($regex.Matches($Before)|ForEach-Object Value)
    $afterTokens=@($regex.Matches($After)|ForEach-Object Value)
    $beforeFixed=@($beforeTokens|Where-Object {$_ -notmatch $movable})
    $afterFixed=@($afterTokens|Where-Object {$_ -notmatch $movable})
    [string[]]$beforeVariables=@($beforeTokens|Where-Object {$_ -match $movable})
    [string[]]$afterVariables=@($afterTokens|Where-Object {$_ -match $movable})
    [Array]::Sort($beforeVariables,[StringComparer]::Ordinal)
    [Array]::Sort($afterVariables,[StringComparer]::Ordinal)
    $strict=(($beforeFixed -join "`0") -ceq ($afterFixed -join "`0") -and ($beforeVariables -join "`0") -ceq ($afterVariables -join "`0"))
    if($strict){return $true}
    if(-not $Key){return $false}
    # A reviewed markup repair is allowed only for its exact KEY and exact
    # approved before/after values. This does not suppress other token drift.
    $approvalPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\translation-policy-overrides.json'
    if(-not (Test-Path -LiteralPath $approvalPath)){return $false}
    if(-not $script:ReusMarkupApprovals){
        $script:ReusMarkupApprovals=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach($entry in (Get-Content -LiteralPath $approvalPath -Raw -Encoding UTF8|ConvertFrom-Json)){$script:ReusMarkupApprovals.Add($entry.key,$entry)}
    }
    if(-not $script:ReusMarkupApprovals.ContainsKey($Key)){return $false}
    $approved=$script:ReusMarkupApprovals[$Key]
    if($After -cne $approved.after -or ($Before -cne $approved.before -and $Before -cne $approved.baselineBefore)){return $false}
    $variables=[regex]'(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]+)?\}(?!\})'
    [string[]]$a=@($variables.Matches($Before)|ForEach-Object Value)
    [string[]]$b=@($variables.Matches($After)|ForEach-Object Value)
    [Array]::Sort($a,[StringComparer]::Ordinal);[Array]::Sort($b,[StringComparer]::Ordinal)
    return (($a -join "`0") -ceq ($b -join "`0"))
}

param([switch]$Build)
$ErrorActionPreference = 'Stop'
$workRoot = $PSScriptRoot
$gameRoot = Split-Path (Split-Path $workRoot -Parent) -Parent
$steamRoot = Join-Path (Split-Path $workRoot -Parent) '20_검수\Steam_6533_완료'
$project = Join-Path $workRoot 'tests\Reus2KRLoaderSmokeTests.csproj'
$csvPath = Join-Path $gameRoot 'BepInEx\plugins\Reus2KR\ko.csv'
$reference = Join-Path $workRoot 'reference'
if ($Build) {
    & (Join-Path $workRoot 'src\build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
}
& dotnet run --project $project -c Release -- --smoke-current $csvPath   # GOG 6,754 current check (legacy 6,533 audit: see 20_검수/Steam_6533_완료)
if ($LASTEXITCODE -ne 0) { throw 'Loader regression failed.' }
& dotnet run --project $project -c Release -- --placeholder-audit $csvPath (Join-Path $reference 'translation_keys.bytes') (Join-Path $reference 'en.bytes')
if ($LASTEXITCODE -ne 0) { throw 'Placeholder audit failed.' }
& dotnet run --project $project -c Release -- --quote-audit $csvPath
if ($LASTEXITCODE -ne 0) { throw 'Quote audit failed.' }
$manifest = Get-Content -LiteralPath (Join-Path $steamRoot 'manifest.json') -Raw | ConvertFrom-Json
$allKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$total = 0
foreach ($phase in $manifest.phases) {
    $phaseFile = Join-Path $steamRoot ('phase-{0:D2}.json' -f [int]$phase.phase)
    $entries = Get-Content -LiteralPath $phaseFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($entries.Count -ne $phase.count) { throw "Phase count mismatch: $($phase.phase)" }
    foreach ($entry in $entries) {
        if (-not $allKeys.Add([string]$entry.key)) { throw 'Duplicate review KEY.' }
        $total++
        if ($entry.ordinal -ne $total) { throw 'Review ordinal mismatch.' }
    }
}
if ($total -ne 6533) { throw 'Review coverage mismatch.' }
$handoffCsv = Join-Path $workRoot 'translation\ko_current.csv'
if ((Get-FileHash -LiteralPath $csvPath).Hash -ne (Get-FileHash -LiteralPath $handoffCsv).Hash) {
    throw 'Installed and handoff CSV differ.'
}
Write-Host 'PASS REVIEW_PARTITIONS: 10 phases, 6533 unique ordered keys'
Write-Host 'PASS INSTALLED_HANDOFF_CSV_HASH'

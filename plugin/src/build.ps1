$ErrorActionPreference = 'Stop'
$game = 'C:\My Games\Reus 2'
$buildRoot = Join-Path $game '.Works\plugin\build'
Set-Location $PSScriptRoot
dotnet build .\Reus2KRBuild.csproj -c Release -p:GameDir="$game" -p:BuildRoot="$buildRoot"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$dll = Join-Path $buildRoot 'bin\Release\netstandard2.1\Reus2KR.dll'
Write-Host "DLL: $dll"

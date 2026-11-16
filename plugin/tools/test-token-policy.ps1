$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'token-policy.ps1')
$cases=@(
    @('{0} {1}','{1} {0}',$true),
    @('{0} {0}','{0}',$false),
    @('{0} {1}','{0} {0}',$false),
    @('{0:N0}','{0:P0}',$false),
    @('[FOOD] {0} [BIO]','[BIO] {0} [FOOD]',$false),
    @('[TAG|{0}] {1}','[TAG|{1}] {0}',$false),
    @('<b>{0}</b> {1}','<b>{1}</b> {0}',$true),
    @('<b>{0}</b>','<i>{0}</i>',$false),
    @('{{0}} {1}','{1} {{0}}',$true),
    @('{name} [FOOD]','[FOOD] {name}',$false)
)
foreach($case in $cases){
    if((Test-ReusProtectedStructure $case[0] $case[1]) -ne $case[2]){throw ('Token policy test failed: '+$case[0])}
}
'PASS TOKEN_POLICY: 10 cases'
$approvalPath=Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\translation-policy-overrides.json'
if(Test-Path -LiteralPath $approvalPath){
    $entry=Get-Content -LiteralPath $approvalPath -Raw -Encoding UTF8|ConvertFrom-Json|Where-Object {$_.key.StartsWith('[QUOTE|')}
    if(-not (Test-ReusProtectedStructure $entry.before $entry.after $entry.key)){throw 'Approved exact repair rejected'}
    if(Test-ReusProtectedStructure $entry.before ($entry.after+' {99}') $entry.key){throw 'Unapproved new variable accepted'}
    if(Test-ReusProtectedStructure $entry.before $entry.after 'not-the-approved-key'){throw 'Wrong-key repair accepted'}
    'PASS EXACT_REPAIR_POLICY: approved repair allowed; unrelated edits/KEY rejected'
}

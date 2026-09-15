param([string]$Configuration='Release',[switch]$Test)
$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot 'src\GameReplay.csproj'
$destination=Join-Path $PSScriptRoot 'artifacts\win-x64'
dotnet restore $project --configfile (Join-Path $PSScriptRoot 'src\NuGet.Config') -r win-x64
if($LASTEXITCODE -ne 0){throw 'Restore failed'}
dotnet publish $project -c $Configuration -r win-x64 --self-contained true --no-restore -o $destination
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
Copy-Item (Join-Path $PSScriptRoot 'LICENSE'),(Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md'),(Join-Path $PSScriptRoot 'README.md') -Destination $destination
Copy-Item (Join-Path $PSScriptRoot 'licenses') -Destination $destination -Recurse -Force
Get-FileHash (Join-Path $destination 'GameReplay.exe') -Algorithm SHA256 | ForEach-Object { $_.Hash.ToLowerInvariant()+'  GameReplay.exe' } | Set-Content (Join-Path $destination 'SHA256SUMS.txt')
if($Test){
 $testRoot=Join-Path $PSScriptRoot 'artifacts\tests'
 foreach($mode in @('--setup-engine','--self-test','--media-test')){
  $process=Start-Process -FilePath (Join-Path $destination 'GameReplay.exe') -ArgumentList $mode,('"'+$testRoot+'"') -WindowStyle Hidden -PassThru -Wait
  if($process.ExitCode -ne 0){throw "Validation failed: $mode. See $testRoot\error.txt"}
 }
}
Write-Output "Built $destination\GameReplay.exe"


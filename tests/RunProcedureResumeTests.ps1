param([string]$BuildTools = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin',
    [string]$Sdk = 'C:\Program Files\VisionMaster4.4.50\Development\V4.x\ComControls\Assembly')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    New-Item -ItemType Directory -Force -Path test-results | Out-Null
    & (Join-Path $BuildTools 'MSBuild.exe') SC6000DualMonitor/SC6000DualMonitor.csproj /t:Build /p:Configuration=Release "/p:ReferencePath=$Sdk" /v:minimal /nologo
    if ($LASTEXITCODE) { throw 'Build failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /out:test-results/ProcedureResumeContractTests.exe tests/ProcedureResumeContractTests.cs
    if ($LASTEXITCODE) { throw 'Resume contract compilation failed' }
    & ./test-results/ProcedureResumeContractTests.exe SC6000DualMonitor/bin/Release/SC6000DualMonitor.exe $Sdk
    if ($LASTEXITCODE) { throw 'Resume contract tests failed' }
}
finally { Pop-Location }

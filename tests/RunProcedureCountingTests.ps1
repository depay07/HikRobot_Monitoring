param([string]$BuildTools = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin',
    [string]$Sdk = 'C:\Program Files\VisionMaster4.4.50\Development\V4.x\ComControls\Assembly')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    New-Item -ItemType Directory -Force -Path test-results | Out-Null
    & (Join-Path $BuildTools 'MSBuild.exe') SC6000DualMonitor/SC6000DualMonitor.csproj /t:Build /p:Configuration=Release "/p:ReferencePath=$Sdk" /v:minimal /nologo
    if ($LASTEXITCODE) { throw 'Build failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /out:test-results/ProcedureCountingTests.exe tests/ProcedureCountingTests.cs
    if ($LASTEXITCODE) { throw 'Counting test compilation failed' }
    & ./test-results/ProcedureCountingTests.exe SC6000DualMonitor/bin/Release/SC6000DualMonitor.exe $Sdk
    if ($LASTEXITCODE) { throw 'Counting tests failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /out:test-results/InspectionLogicTests.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll SC6000DualMonitor/InspectionModel.cs tests/InspectionLogicTests.cs
    if ($LASTEXITCODE) { throw 'Inspection logic compilation failed' }
    & ./test-results/InspectionLogicTests.exe
    if ($LASTEXITCODE) { throw 'Inspection logic tests failed' }
}
finally { Pop-Location }

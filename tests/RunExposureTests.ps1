param([string]$BuildTools = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin',
    [string]$Sdk = 'C:\Program Files\VisionMaster4.4.50\Development\V4.x\ComControls\Assembly')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    New-Item -ItemType Directory -Force -Path test-results | Out-Null
    & (Join-Path $BuildTools 'MSBuild.exe') SC6000DualMonitor/SC6000DualMonitor.csproj /t:Build /p:Configuration=Release "/p:ReferencePath=$Sdk" /v:minimal /nologo
    if ($LASTEXITCODE) { throw 'Release build failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /out:test-results/ExposureSettingsTests.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll SC6000DualMonitor/ExposureSettings.cs SC6000DualMonitor/ExposureSetupForm.cs tests/ExposureSettingsTests.cs
    if ($LASTEXITCODE) { throw 'Exposure tests compilation failed' }
    & ./test-results/ExposureSettingsTests.exe "$PWD/test-results/exposure-preview.png"
    if ($LASTEXITCODE) { throw 'Exposure tests failed' }
}
finally { Pop-Location }

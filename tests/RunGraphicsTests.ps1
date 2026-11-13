param([string]$BuildTools = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin',
    [string]$Sdk = 'C:\Program Files\VisionMaster4.4.50\Development\V4.x\ComControls\Assembly')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    New-Item -ItemType Directory -Force -Path test-results | Out-Null
    & (Join-Path $BuildTools 'MSBuild.exe') SC6000DualMonitor/SC6000DualMonitor.csproj /t:Build /p:Configuration=Release "/p:ReferencePath=$Sdk" /v:minimal /nologo
    if ($LASTEXITCODE) { throw 'Build failed' }
    $wpf = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /out:test-results/GraphicsToggleTests.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$wpf\WindowsBase.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\PresentationFramework.dll" "/r:$wpf\WindowsFormsIntegration.dll" SC6000DualMonitor/FrontendGraphics.cs tests/GraphicsToggleTests.cs
    if ($LASTEXITCODE) { throw 'Graphics test compilation failed' }
    & ./test-results/GraphicsToggleTests.exe $Sdk SC6000DualMonitor/bin/Release/SC6000DualMonitor.exe
    if ($LASTEXITCODE) { throw 'Graphics tests failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /out:test-results/DisplayRecoveryTests.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll tests/DisplayRecoveryTests.cs
    if ($LASTEXITCODE) { throw 'Display test compilation failed' }
    & ./test-results/DisplayRecoveryTests.exe SC6000DualMonitor/bin/Release/SC6000DualMonitor.exe
    if ($LASTEXITCODE) { throw 'Display recovery tests failed' }
}
finally { Pop-Location }


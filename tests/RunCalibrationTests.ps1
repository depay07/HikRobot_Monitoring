param(
    [string]$Sdk = 'C:\Program Files\VisionMaster4.4.50\Development\V4.x\ComControls\Assembly',
    [string]$BuildTools = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin'
)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    New-Item -ItemType Directory -Force -Path test-results | Out-Null
    $compiler = Join-Path $BuildTools 'Roslyn\csc.exe'
    & (Join-Path $BuildTools 'MSBuild.exe') SC6000DualMonitor/SC6000DualMonitor.csproj /t:Rebuild /p:Configuration=Release "/p:ReferencePath=$Sdk" /v:minimal /nologo
    if ($LASTEXITCODE) { throw 'Release build failed' }
    & $compiler /nologo /out:test-results/CalibrationTests.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll SC6000DualMonitor/CalibrationModel.cs SC6000DualMonitor/CalibrationMapping.cs SC6000DualMonitor/CalibrationForm.cs tests/CalibrationTests.cs
    if ($LASTEXITCODE) { throw 'Calibration tests compilation failed' }
    & ./test-results/CalibrationTests.exe "$PWD/test-results/calibration-preview.png"
    if ($LASTEXITCODE) { throw 'Calibration tests failed' }
    & $compiler /nologo /out:test-results/CalibrationSdkTests.exe tests/CalibrationSdkTests.cs
    if ($LASTEXITCODE) { throw 'SDK tests compilation failed' }
    & ./test-results/CalibrationSdkTests.exe SC6000DualMonitor/bin/Release/SC6000DualMonitor.exe $Sdk
    if ($LASTEXITCODE) { throw 'SDK callback tests failed' }
    & $compiler /nologo /out:test-results/CalibrationMappingUiTests.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll tests/CalibrationMappingUiTests.cs
    if ($LASTEXITCODE) { throw 'Mapping UI tests compilation failed' }
    & ./test-results/CalibrationMappingUiTests.exe SC6000DualMonitor/bin/Release/SC6000DualMonitor.exe $Sdk "$PWD/test-results/calibration-mapping-preview.png"
    if ($LASTEXITCODE) { throw 'Mapping UI tests failed' }
    $references = @('VM.Core','VM.PlatformSDKCS','VMControls.BaseInterface','VMControls.Interface','GlobalVariableModuleCs') | ForEach-Object { '/r:' + (Join-Path $Sdk ($_ + '.dll')) }
    & $compiler /nologo /out:test-results/CalibrationInventoryTests.exe /r:System.Web.Extensions.dll $references SC6000DualMonitor/CalibrationDeviceData.cs tests/CalibrationInventoryTests.cs
    if ($LASTEXITCODE) { throw 'Inventory tests compilation failed' }
    & ./test-results/CalibrationInventoryTests.exe $Sdk
    if ($LASTEXITCODE) { throw 'Inventory tests failed' }
    & $compiler /nologo /out:test-results/CurrentRecipeSaveTests.exe SC6000DualMonitor/CurrentRecipeSave.cs tests/CurrentRecipeSaveTests.cs
    if ($LASTEXITCODE) { throw 'Recipe save tests compilation failed' }
    & ./test-results/CurrentRecipeSaveTests.exe
    if ($LASTEXITCODE) { throw 'Recipe save tests failed' }
}
finally { Pop-Location }

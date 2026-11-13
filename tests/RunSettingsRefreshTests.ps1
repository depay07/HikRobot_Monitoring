param([string]$BuildTools = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    New-Item -ItemType Directory -Force -Path test-results | Out-Null
    $compiler = Join-Path $BuildTools 'Roslyn\csc.exe'
    $sources = @('SC6000DualMonitor/SettingsSaveCompletion.cs', 'SC6000DualMonitor/ExposureSettings.cs', 'SC6000DualMonitor/ExposureSetupForm.cs',
        'SC6000DualMonitor/BlobSettings.cs', 'SC6000DualMonitor/BlobSetupForm.cs', 'SC6000DualMonitor/InspectionModel.cs', 'SC6000DualMonitor/InspectionSetupForm.cs', 'SC6000DualMonitor/ConditionParameterReader.cs')
    & $compiler /nologo /out:test-results/SettingsRefreshTests.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll $sources tests/SettingsRefreshTests.cs
    if ($LASTEXITCODE) { throw 'Settings refresh test compilation failed' }
    & ./test-results/SettingsRefreshTests.exe
    if ($LASTEXITCODE) { throw 'Settings refresh tests failed' }
    foreach ($test in @('BlobSettingsTests', 'SetupReloadTests', 'InspectionLogicTests')) {
        & $compiler /nologo "/out:test-results/$test.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll $sources "tests/$test.cs"
        if ($LASTEXITCODE) { throw "$test compilation failed" }
        & "./test-results/$test.exe"
        if ($LASTEXITCODE) { throw "$test failed" }
    }
}
finally { Pop-Location }

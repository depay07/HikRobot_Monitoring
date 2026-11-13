param([string]$BuildTools = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin',
    [string]$Sdk = 'C:\Program Files\VisionMaster4.4.50\Development\V4.x\ComControls\Assembly')
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    New-Item -ItemType Directory -Force -Path test-results | Out-Null
    & (Join-Path $BuildTools 'MSBuild.exe') SC6000DualMonitor/SC6000DualMonitor.csproj /t:Build /p:Configuration=Release "/p:ReferencePath=$Sdk" /v:minimal /nologo
    if ($LASTEXITCODE) { throw 'Release build failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /out:test-results/RecipeFileManagerTests.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll SC6000DualMonitor/RecipeFileManager.cs SC6000DualMonitor/RecipeFilesForm.cs tests/RecipeFileManagerTests.cs
    if ($LASTEXITCODE) { throw 'Recipe file tests compilation failed' }
    & ./test-results/RecipeFileManagerTests.exe "$PWD/test-results/recipe-files-preview.png"
    if ($LASTEXITCODE) { throw 'Recipe file tests failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /out:test-results/FtpRecipeTests.exe SC6000DualMonitor/RecipeFileManager.cs SC6000DualMonitor/FtpRecipeSession.cs tests/FtpRecipeTests.cs
    if ($LASTEXITCODE) { throw 'FTPES tests compilation failed' }
    & ./test-results/FtpRecipeTests.exe
    if ($LASTEXITCODE) { throw 'FTPES tests failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /platform:x86 /out:test-results/RecipeFileSdkTests.exe tests/RecipeFileSdkTests.cs
    if ($LASTEXITCODE) { throw 'SDK FTP tests compilation failed' }
    & ./test-results/RecipeFileSdkTests.exe SC6000DualMonitor/bin/Release/SC6000DualMonitor.exe $Sdk
    if ($LASTEXITCODE) { throw 'SDK FTP tests failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /platform:x64 /out:test-results/FtpNativeLoadTests.exe SC6000DualMonitor/RecipeFileManager.cs SC6000DualMonitor/FtpRecipeSession.cs SC6000DualMonitor/NativeCameraFtp.cs tests/FtpNativeLoadTests.cs
    if ($LASTEXITCODE) { throw 'x64 FTP load test compilation failed' }
    & ./test-results/FtpNativeLoadTests.exe $Sdk
    if ($LASTEXITCODE) { throw 'x64 FTP load test failed' }
    & (Join-Path $BuildTools 'Roslyn\csc.exe') /nologo /platform:x86 /out:test-results/FtpNativeLoadTests-x86.exe SC6000DualMonitor/RecipeFileManager.cs SC6000DualMonitor/FtpRecipeSession.cs SC6000DualMonitor/NativeCameraFtp.cs tests/FtpNativeLoadTests.cs
    if ($LASTEXITCODE) { throw 'x86 FTP protocol test compilation failed' }
    & ./test-results/FtpNativeLoadTests-x86.exe $Sdk
    if ($LASTEXITCODE) { throw 'x86 FTP protocol test failed' }
}
finally { Pop-Location }

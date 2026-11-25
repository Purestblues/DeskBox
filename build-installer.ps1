param(
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "==> 1. 运行单元测试..." -ForegroundColor Cyan
dotnet test --nologo
if ($LASTEXITCODE -ne 0) { throw "单元测试未通过，中止打包。" }

$stamp = Get-Date -Format "yyyyMMddHHmmss"
$publishDir = Join-Path $root "artifacts\publish_$stamp"
$setupResDir = Join-Path $root "src\DeskBox.Setup\Resources"
$payloadZip = Join-Path $setupResDir "payload.zip"
$setupOutDir = Join-Path $root "artifacts\setup_out_$stamp"
$distDir = Join-Path $root "dist"

New-Item -ItemType Directory -Force -Path $publishDir, $setupResDir, $setupOutDir, $distDir | Out-Null

$scFlag = if ($SelfContained) { "--self-contained" } else { "--no-self-contained" }

Write-Host "==> 2. 发布 DeskBox 主程序 ($scFlag)..." -ForegroundColor Cyan
dotnet publish "src\DeskBox\DeskBox.csproj" -c Release -r win-x64 $scFlag -p:DebugType=none -p:DebugSymbols=false -o $publishDir --nologo
if ($LASTEXITCODE -ne 0) { throw "主程序发布失败。" }

Write-Host "==> 3. 打包 payload.zip..." -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$fs = [System.IO.File]::Create($payloadZip)
try {
    $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $baseLen = $publishDir.TrimEnd('\').Length + 1
        foreach ($file in Get-ChildItem -Path $publishDir -Recurse -File) {
            $rel = $file.FullName.Substring($baseLen)
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
    }
}
finally {
    $fs.Dispose()
}

Write-Host "==> 4. 构建单文件安装向导 DeskBox-Setup..." -ForegroundColor Cyan
dotnet publish "src\DeskBox.Setup\DeskBox.Setup.csproj" -c Release -r win-x64 $scFlag -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o $setupOutDir --nologo
if ($LASTEXITCODE -ne 0) { throw "安装程序编译失败。" }

$setupExe = Join-Path $setupOutDir "DeskBox.Setup.exe"
$suffix = if ($SelfContained) { "win-x64-standalone" } else { "win-x64" }
$finalInstaller = Join-Path $distDir "DeskBox-Setup-1.2.1-$suffix.exe"
Copy-Item $setupExe $finalInstaller -Force

$sizeMB = [math]::Round((Get-Item $finalInstaller).Length / 1MB, 2)
Write-Host "==> 打包完成: $finalInstaller ($sizeMB MB)" -ForegroundColor Green

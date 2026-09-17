# ffmpeg (LGPL ビルド) を取得して tools/ffmpeg/bin/ に配置する。
# 用途: spike/gate-a-check（無音検査）・spike/video-compose-check（R-05 動画合成）
# ライセンス: FFmpeg は LGPL 版（BtbN FFMBuilds）を使用。 THIRD_PARTY_NOTICES.md を参照。
#
# 使い方: powershell -ExecutionPolicy Bypass -File tools\get-ffmpeg.ps1

$ErrorActionPreference = 'Stop'

$dest = Join-Path $PSScriptRoot 'ffmpeg\bin'
if (Test-Path (Join-Path $dest 'ffmpeg.exe')) {
    Write-Host "ffmpeg は既に存在します: $dest"
    exit 0
}

$url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip'
$zip = Join-Path $env:TEMP 'ffmpeg-lgpl-win64.zip'

Write-Host "ダウンロード中: $url"
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

$extractRoot = Join-Path $env:TEMP 'ffmpeg-lgpl-extract'
if (Test-Path $extractRoot) { Remove-Item -Recurse -Force $extractRoot }
Expand-Archive -Path $zip -DestinationPath $extractRoot -Force

New-Item -ItemType Directory -Force $dest | Out-Null
# zip 内はバージョン付きフォルダ（ffmpeg-master-latest-win64-lgpl/bin/...）なので bin 配下だけ拾う
$binDir = Get-ChildItem $extractRoot -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
if ($null -eq $binDir) { throw 'ffmpeg.exe が zip 内に見つかりません' }
$srcBin = $binDir.DirectoryName
Copy-Item (Join-Path $srcBin '*.exe') $dest
# LGPL 版に必要な DLL もコピー（avcodec/avformat 等を exe が直接持つ場合は無いが念のため）
Get-ChildItem $srcBin -Filter '*.dll' | Copy-Item -Destination $dest -ErrorAction SilentlyContinue

Remove-Item -Recurse -Force $extractRoot, $zip -ErrorAction SilentlyContinue
Write-Host "完了: $dest"

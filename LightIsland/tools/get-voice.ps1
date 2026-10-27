# Runs only where LightIsland is built. Puts the offline voice engines next to the app:
#   voice\whisper\  whisper.cpp (MIT)      speech -> text
#   voice\llama\    llama.cpp  (MIT)       small language model runner
#   voice\models\   ggml-base.bin (MIT, OpenAI Whisper base) and Qwen2.5 1.5B Instruct Q4 (Apache-2.0)
param(
    [Parameter(Mandatory)][string]$Release
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$cache = Join-Path $PSScriptRoot '.cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$ua = @{ 'User-Agent' = 'LightIsland-build' }
$voice = Join-Path $Release 'voice'
New-Item -ItemType Directory -Force -Path $voice | Out-Null

function Get-File($url, $name) {
    $p = Join-Path $cache $name
    if (-not (Test-Path $p) -or (Get-Item $p).Length -lt 1000) {
        Write-Host "  downloading $name ..."
        Invoke-WebRequest -Uri $url -OutFile $p -Headers $ua -UseBasicParsing
    }
    return $p
}

function Unpack($zip, $dest) {
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    $tmp = Join-Path $cache ('x-' + [IO.Path]::GetFileNameWithoutExtension($zip))
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    # flatten: the folder that holds the .exe files
    $exe = Get-ChildItem $tmp -Recurse -Filter *.exe | Select-Object -First 1
    Copy-Item (Join-Path $exe.Directory.FullName '*') $dest -Recurse -Force
}

Write-Host '[voice 1/4] whisper.cpp'
# the newest release that ships Windows x64 binaries (not every release does)
$a = $null
$names = @()
foreach ($rel in (Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=15' -Headers $ua)) {
    $names += ($rel.tag_name + ': ' + (($rel.assets | ForEach-Object { $_.name }) -join ' '))
    $a = $rel.assets | Where-Object { $_.name -match '^whisper-bin-x64\.zip$' } | Select-Object -First 1
    if ($a) { break }
}
if (-not $a) { throw ('whisper.cpp: no Windows x64 zip. Releases seen: ' + ($names -join ' | ')) }
Unpack (Get-File $a.browser_download_url $a.name) (Join-Path $voice 'whisper')

Write-Host '[voice 2/4] llama.cpp'
$rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/llama.cpp/releases/latest' -Headers $ua
Write-Host ("  assets: " + (($rel.assets | ForEach-Object { $_.name } | Where-Object { $_ -match 'win' }) -join ', '))
$a = $rel.assets | Where-Object { $_.name -match 'bin-win-cpu-x64\.zip$' } | Select-Object -First 1
if (-not $a) { $a = $rel.assets | Where-Object { $_.name -match 'win.*(avx2|cpu).*x64.*\.zip$' -and $_.name -notmatch 'cudart|cuda|vulkan|hip|sycl|opencl' } | Select-Object -First 1 }
if (-not $a) { throw 'llama.cpp: no Windows CPU zip in the latest release' }
Unpack (Get-File $a.browser_download_url $a.name) (Join-Path $voice 'llama')

Write-Host '[voice 3/4] speech model'
$models = Join-Path $voice 'models'
New-Item -ItemType Directory -Force -Path $models | Out-Null
Copy-Item (Get-File 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin' 'ggml-base.bin') (Join-Path $models 'ggml-base.bin') -Force

Write-Host '[voice 4/4] language model (Qwen2.5 1.5B Instruct, Q4)'
Copy-Item (Get-File 'https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf' 'qwen2.5-1.5b-instruct-q4_k_m.gguf') (Join-Path $models 'qwen2.5-1.5b-instruct-q4_k_m.gguf') -Force

'Voice engines: whisper.cpp (MIT), llama.cpp (MIT), OpenAI Whisper base model (MIT), Qwen2.5-1.5B-Instruct (Apache-2.0).' | Set-Content (Join-Path $voice 'NOTICE.txt')
Write-Host 'Voice engines ready.'

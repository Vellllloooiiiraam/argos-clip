# ---------------------------------------------------------------------------
#  Teste de fumaca de ponta a ponta:
#   sobe o app numa porta de teste, envia o video, acompanha o status e
#   confere se os clipes foram gerados.
# ---------------------------------------------------------------------------
param(
    [Parameter(Mandatory = $true)][string]$Video,
    [int]$Port = 8599,
    [string]$Dll = "src\ArgosClip\bin\Debug\net8.0-windows\argos-clip.dll",
    [string]$Dotnet = "dotnet",
    [int]$NClips = 2,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent $PSScriptRoot
$dllPath = Join-Path $root $Dll
if (-not (Test-Path $dllPath)) { throw "Executavel nao encontrado: $dllPath" }
$isExe = $dllPath.ToLower().EndsWith(".exe")

$base = "http://127.0.0.1:$Port"
$jobs = Join-Path $env:TEMP "argos-clip-test\jobs"
New-Item -ItemType Directory -Force -Path $jobs | Out-Null

# --- sobe o servidor ---------------------------------------------------------
$env:ARGOS_PORT = "$Port"
$env:ARGOS_HEADLESS = "1"
$env:ARGOS_JOBS_DIR = $jobs
if (-not $env:ARGOS_WHISPER_MODEL) { $env:ARGOS_WHISPER_MODEL = "tiny" }

function Stop-Server {
    if ($proc -and -not $proc.HasExited) {
        try { $proc.Kill($true) } catch { }
    }
    # garante que nada continue preso na porta de teste
    try {
        Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
            ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
    } catch { }
}
trap { Write-Host "  [ERRO] $_" -ForegroundColor Red; Stop-Server; exit 1 }

# limpa qualquer processo antigo na porta de teste
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 500

Write-Host "  subindo o servidor em $base ..."
$exePath = if ($isExe) { $dllPath } else { $Dotnet }
$wd = Split-Path -Parent $dllPath
if ($isExe) {
    $proc = Start-Process -FilePath $exePath -WorkingDirectory $wd -PassThru -WindowStyle Hidden
} else {
    $proc = Start-Process -FilePath $exePath -ArgumentList @($dllPath) -WorkingDirectory $wd -PassThru -WindowStyle Hidden
}

# espera o servidor responder
$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromSeconds(10)
$up = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    try {
        $r = $http.GetAsync("$base/").GetAwaiter().GetResult()
        if ($r.IsSuccessStatusCode) { $up = $true; break }
    } catch { }
}
if (-not $up) { throw "O servidor nao subiu em $base." }
Write-Host "  servidor no ar."

# --- upload ------------------------------------------------------------------
Write-Host "  enviando $Video ..."
$content = New-Object System.Net.Http.MultipartFormDataContent
$stream = [System.IO.File]::OpenRead($Video)
$fileContent = New-Object System.Net.Http.StreamContent($stream)
$content.Add($fileContent, "file", [System.IO.Path]::GetFileName($Video))
$content.Add((New-Object System.Net.Http.StringContent("$NClips")), "n_clips")
$content.Add((New-Object System.Net.Http.StringContent("pt")), "language")

$resp = $http.PostAsync("$base/api/upload", $content).GetAwaiter().GetResult()
$body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
$stream.Dispose()
if (-not $resp.IsSuccessStatusCode) { throw "upload falhou ($([int]$resp.StatusCode)): $body" }

$jobId = ($body | ConvertFrom-Json).job_id
Write-Host "  job: $jobId"

# --- acompanha o status ------------------------------------------------------
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$lastStage = ""
$status = $null
while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    Start-Sleep -Seconds 2
    $st = $http.GetStringAsync("$base/api/status/$jobId").GetAwaiter().GetResult() | ConvertFrom-Json
    if ($st.stage -ne $lastStage) {
        Write-Host ("  [{0,4:N0}s] {1}" -f $sw.Elapsed.TotalSeconds, $st.stage)
        $lastStage = $st.stage
    }
    if ($st.status -eq "done") { $status = $st; break }
    if ($st.status -eq "error") { throw "job falhou: $($st.error)" }
}

if (-not $status) { throw "tempo esgotado (${TimeoutSeconds}s) sem concluir." }

# --- valida ------------------------------------------------------------------
Write-Host ""
Write-Host "  clipes gerados: $($status.clips.Count)"
$ok = $true
foreach ($c in $status.clips) {
    $url = "$base/media/$jobId/$($c.video)"
    $h = $http.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    $len = 0
    if ($h.Content.Headers.ContentLength) { $len = $h.Content.Headers.ContentLength }
    Write-Host ("   #{0}  {1}  {2}s  score {3}  {4} bytes" -f $c.index, $c.video, $c.duration, $c.score, $len)
    if ($len -le 0) { $ok = $false }
}

Stop-Server
if ($ok -and $status.clips.Count -gt 0) {
    Write-Host ""
    Write-Host "  RESULTADO: OK - o pipeline gerou clipes validos."
    exit 0
} else {
    Write-Host ""
    Write-Host "  RESULTADO: FALHOU - nenhum clipe valido."
    exit 1
}

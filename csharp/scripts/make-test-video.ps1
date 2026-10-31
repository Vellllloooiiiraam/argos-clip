# ---------------------------------------------------------------------------
#  Gera um video de teste (com fala real) usando a voz do Windows + FFmpeg.
#  Sem argumentos, cria <pasta temp>\argos-clip-test\test.mp4
# ---------------------------------------------------------------------------
param(
    [string]$OutDir = (Join-Path $env:TEMP "argos-clip-test")
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$wav   = Join-Path $OutDir "speech.wav"
$video = Join-Path $OutDir "test.mp4"

if (Test-Path $video) {
    Write-Host "  video de teste ja existe: $video"
    exit 0
}

Write-Host "  sintetizando fala..."
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.Rate = 0
$synth.SetOutputToWaveFile($wav)
$texto = @"
Ola, este e um teste do Argos Clip. Nunca faca isso errado.
Existe um segredo importante aqui. O erro mais comum e nao praticar todos os dias.
Voce precisa saber disso agora. Tres dicas simples vao mudar tudo no seu resultado.
Primeiro, sempre teste antes de confiar no processo. Segundo, o metodo funciona quando
voce tem disciplina e foco. Terceiro, ninguem alcanca resultado sem esforco real.
Por que isso acontece? Porque o processo e mais importante que a meta final.
Dinheiro nao cai do ceu, mas a estrategia certa acelera tudo. Descobri isso depois
de anos de erro e de fracasso. Entao comece hoje mesmo, sem desculpas, e veja a
diferenca no seu resultado. Obrigado por assistir este video ate o final.
"@
$synth.Speak($texto)
$synth.Dispose()

Write-Host "  montando o mp4 com FFmpeg..."
& ffmpeg -y -hide_banner -loglevel error `
    -f lavfi -i "testsrc2=size=1280x720:rate=30" `
    -i $wav -shortest `
    -c:v libx264 -preset veryfast -pix_fmt yuv420p `
    -c:a aac -b:a 128k $video

if ($LASTEXITCODE -ne 0) { throw "ffmpeg falhou ao montar o video de teste." }
Write-Host "  ok: $video"

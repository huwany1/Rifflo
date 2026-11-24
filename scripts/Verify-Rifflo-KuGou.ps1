#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Start', 'AutomaticSong', 'Songs', 'Exited', 'Restarted', 'Isolation', 'Finish')]
    [string]$Phase,
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x64-spectrum-verified'),
    [string]$EvidenceDirectory = (Join-Path $PSScriptRoot '..\artifacts\verification\kugou-spectrum'),
    [switch]$UseAutomaticSelection
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Rifflo.Verification.Helpers.ps1')

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$manifestPath = Join-Path $EvidenceDirectory 'run.json'
$trial = $null
$script:rifflo = $null
$script:edge = $null
$script:edgeIds = @()
$script:assertions = [System.Collections.Generic.List[object]]::new()
$script:ownedProcesses = [System.Collections.Generic.List[Diagnostics.Process]]::new()
$script:fixture = Join-Path $repo 'tests\Rifflo.PlaybackFixture\bin\Release\net10.0-windows10.0.26100.0\Rifflo.PlaybackFixture.exe'
$script:exe = [IO.Path]::GetFullPath((Join-Path $PublishDirectory 'Rifflo.exe'))
$edgePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
$edgeTitle = 'Rifflo Edge 6000 Hz'
$steadySamples = 5
$settleMs = 2400
$audibleFloor = 0.01
$silenceCeiling = 0.001
$visualFloor = 0.01
$lowDominantLimit = 0.45
$highDominantLimit = 0.65
$highRegionStart = 0.64
$isolationRatio = 0.25
$inputRatioLimit = 4.0
$errorLog = Join-Path $EvidenceDirectory "$($Phase.ToLowerInvariant()).error.json"
Remove-Item -LiteralPath $errorLog -ErrorAction SilentlyContinue

function Get-LiveDiagnostics {
    $sample = Read-JsonSnapshot $script:diagPath
    if ($sample -and $sample.title -and $sample.source -ne 'kugou' -and $sample.title -cne $edgeTitle) {
        $sample.title = '[existing desktop media session]'
        $sample.artist = ''
    }
    $sample
}

function Read-RealSessions {
    $all = & $script:fixture --sessions | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Windows GSMTC probe failed.' }
    @($all.sessions | Where-Object { $_.sourceAppUserModelId -eq 'kugou' -or $_.title -eq $edgeTitle })
}

function Get-KuGouMain {
    Get-Process -Name KuGou -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -and $_.MainWindowTitle -match '酷狗音乐\s*$' } |
        Sort-Object { $_.StartTime } |
        Select-Object -First 1
}

function Get-Integrity([int]$ProcessId) {
    $token = & $script:fixture --integrity $ProcessId | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Cannot read token of PID $ProcessId." }
    $token
}

function Test-MediumToken([object]$Token) {
    !$Token.elevated -and $Token.integrityRid -eq 8192 -and $Token.sessionId -gt 0
}

function Wait-RealTrack([scriptblock]$Condition) {
    Wait-Observation 'KuGou GSMTC' { Read-RealSessions | Where-Object sourceAppUserModelId -EQ 'kugou' } $Condition 20
}

function Set-KuGouTransport([string]$Command) {
    $reply = & $script:fixture --media kugou $Command | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or !$reply.acknowledged) { throw "KuGou declined the GSMTC command $Command." }
}

function Get-Bands([object]$Diagnostic) {
    $bands = @($Diagnostic.bands)
    $dominant = 0
    $highPeak = 0.0
    for ($index = 0; $index -lt $bands.Count; $index++) {
        if ($bands[$index] -gt $bands[$dominant]) { $dominant = $index }
        if ($index / ($bands.Count - 1) -ge $highRegionStart) { $highPeak = [Math]::Max($highPeak, $bands[$index]) }
    }
    [pscustomobject]@{
        dominant = $dominant
        normalizedIndex = $dominant / ($bands.Count - 1)
        peak = $bands[$dominant]
        highPeak = $highPeak
    }
}

function Read-LiveSample([object]$Diagnostic) {
    $time = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $audio = Get-AudioSessions
    $meterEnd = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $media = @(Read-RealSessions)
    $kgIds = @(Get-Process -Name KuGou -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $kg = @($audio.sessions | Where-Object { $_.pid -in $kgIds })
    $edge = @($audio.sessions | Where-Object { $_.pid -in $script:edgeIds })
    $result = [pscustomobject]@{
        unixTimeMs = $time
        meterFinishedUnixTimeMs = $meterEnd
        endpointId = $audio.endpointId
        gsmTc = $media
        kugouAudio = $kg
        edgeAudio = $edge
        kugouPeak = (@(0) + @($kg | ForEach-Object peak) | Measure-Object -Maximum).Maximum
        edgePeak = (@(0) + @($edge | ForEach-Object peak) | Measure-Object -Maximum).Maximum
        rifflo = $Diagnostic
        bandEvidence = Get-Bands $Diagnostic
    }
    $result
}

function Read-SteadyWindow([string]$Label, [int]$SettleMilliseconds = $settleMs, [int]$Count = $steadySamples) {
    $after = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() + $SettleMilliseconds
    $series = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $Count; $index++) {
        $timeout = 6 + [int][Math]::Ceiling([Math]::Max(0, $after - [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) / 1000)
        $diagnostic = Wait-Observation "new diagnostics for $Label" { Get-LiveDiagnostics } {
            param($s) $s.unixTimeMs -gt $after
        } $timeout
        $series.Add((Read-LiveSample $diagnostic))
        $after = $diagnostic.unixTimeMs
    }
    Save-Evidence "$Label.samples.json" $series.ToArray()
    $series.ToArray()
}

function Test-All([object[]]$Items, [scriptblock]$Condition) {
    $Items.Count -gt 0 -and @($Items | Where-Object { !(& $Condition $_) }).Count -eq 0
}

function Test-PlayingKuGou([object]$Sample) {
    $kg = $Sample.gsmTc | Where-Object sourceAppUserModelId -EQ 'kugou' | Select-Object -First 1
    $null -ne $kg -and $kg.status -eq 'Playing' -and $Sample.rifflo.state -eq 'Playing' -and
        $Sample.rifflo.source -eq 'kugou' -and $Sample.rifflo.title -ceq $kg.title -and
        $Sample.rifflo.artist -ceq $kg.artist -and $Sample.rifflo.visible
}

function Test-AudibleKuGouCapture([object]$Sample) {
    if (!(Test-PlayingKuGou $Sample) -or !$Sample.rifflo.capturing -or !$Sample.rifflo.spectrumVisible) { return $false }
    $match = $Sample.rifflo.audioMatch
    $active = @($Sample.kugouAudio | Where-Object { $_.state -eq 1 -and !$_.muted -and $_.peak -ge $audibleFloor })
    $null -ne $match -and $match.approved -and $match.recording -and $active.Count -gt 0 -and
        ($match.targetProcessId -in $active.pid -or $match.instanceProcessId -in $active.pid -or
         @($match.sessions | Where-Object { $_.processId -in $active.pid -and $_.endpointId -eq $Sample.endpointId }).Count -gt 0)
}

function Test-AudibleEdge([object]$Sample) {
    $media = $Sample.gsmTc | Where-Object title -EQ $edgeTitle | Select-Object -First 1
    $null -ne $media -and $media.status -eq 'Playing' -and
        @($Sample.edgeAudio | Where-Object { $_.state -eq 1 -and !$_.muted -and $_.peak -ge $audibleFloor }).Count -gt 0
}

function Test-AdvancingCounts([object[]]$Samples) {
    for ($index = 1; $index -lt $Samples.Count; $index++) {
        if ($Samples[$index].rifflo.packetCount -le $Samples[$index - 1].rifflo.packetCount -or
            $Samples[$index].rifflo.fftCount -le $Samples[$index - 1].rifflo.fftCount) { return $false }
    }
    $Samples.Count -ge 2
}

function Save-DynamicSong([string]$Label, [object[]]$Samples, [string]$ExpectedTitle) {
    $arrays = @($Samples | ForEach-Object {
        [string]::Join(',', [double[]]($_.rifflo.bands | ForEach-Object { [Math]::Round($_, 3) }))
    } | Sort-Object -Unique)
    $first = $Samples[0].rifflo
    $last = $Samples[-1].rifflo
    $matches = Test-All $Samples {
        param($s) (Test-AudibleKuGouCapture $s) -and $s.rifflo.title -ceq $ExpectedTitle -and
            $s.rifflo.artist.Length -gt 0 -and $s.bandEvidence.peak -gt $visualFloor
    }
    Add-Assertion "$Label shows KuGou's exact song and artist with continuously advancing and changing spectrum" (
        $matches -and (Test-AdvancingCounts $Samples) -and $arrays.Count -ge 3
    ) @{
        title = $last.title; artist = $last.artist; uniqueBandArrays = $arrays.Count
        spanMs = $last.unixTimeMs - $first.unixTimeMs; packetDelta = $last.packetCount - $first.packetCount
        fftDelta = $last.fftCount - $first.fftCount; match = $last.audioMatch
    }
}

function Save-SongPicture([object]$Sample) {
    # The owned dark surface keeps the crop independent of windows behind the transparent overlay.
    Add-Type -Path (Join-Path $PSScriptRoot 'Rifflo.Verification.Native.cs')
    Add-Type -AssemblyName System.Drawing.Common
    $window = Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗'
    if (!$window) { throw 'The release overlay was not visible for the picture.' }
    $bounds = $window.Bounds
    $left = $bounds.Left - 8
    $top = $bounds.Top - 8
    $width = [Math]::Min(400, $bounds.Right - $bounds.Left + 16)
    $height = $bounds.Bottom - $bounds.Top + 16
    $hitLog = Join-Path $trial.tempDirectory 'surface-clicks.txt'
    $background = Register-TestProcess (Start-Process -FilePath $script:fixture -ArgumentList @(
        '--surface', $left, $top, $width, $height, 'dark', "`"$hitLog`""
    ) -WindowStyle Hidden -PassThru)
    Wait-Observation 'dark crop background' { Find-RiffloWindow $background 'Rifflo verification click surface' } { $true } 8 | Out-Null
    Start-Sleep -Milliseconds 450
    $observed = Get-LiveDiagnostics
    if (!$observed.capturing -or $observed.title -cne $Sample.rifflo.title) { throw 'Song changed before overlay image capture.' }
    Save-ScreenCrop 'real-song-overlay.png' $left $top $width $height
    Save-Evidence 'real-song-overlay.json' ([pscustomobject]@{
        screenshotUnixTimeMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        diagnostics = $observed
    })
    Stop-TestProcess $background
}

function Set-RealTone([string]$Title) {
    $file = Join-Path $trial.tempDirectory "$Title.wav"
    Start-Process -FilePath $trial.kugouPath -ArgumentList "`"$file`""
    Wait-RealTrack { param($s) $s.title -ceq $Title -and $s.status -eq 'Playing' } | Out-Null
}

function Get-EdgeTree([int]$RootProcessId) {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'")
    $ids = [System.Collections.Generic.HashSet[int]]::new()
    $null = $ids.Add($RootProcessId)
    do {
        $count = $ids.Count
        foreach ($process in $processes) {
            if ($ids.Contains($process.ParentProcessId)) { $null = $ids.Add($process.ProcessId) }
        }
    } while ($ids.Count -ne $count)
    @($ids)
}

function Invoke-EdgePlayer([string]$Expression) {
    $socket = [Net.WebSockets.ClientWebSocket]::new()
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(8))
    try {
        $socket.ConnectAsync($script:edgeSocket, $deadline.Token).GetAwaiter().GetResult()
        $command = @{
            id = 1; method = 'Runtime.evaluate'
            params = @{ expression = $Expression; awaitPromise = $true; returnByValue = $true }
        } | ConvertTo-Json -Depth 4 -Compress
        $bytes = [ArraySegment[byte]]::new([Text.Encoding]::UTF8.GetBytes($command))
        $socket.SendAsync($bytes, [Net.WebSockets.WebSocketMessageType]::Text, $true, $deadline.Token).GetAwaiter().GetResult()
        $buffer = [ArraySegment[byte]]::new([byte[]]::new(4096))
        $reply = [IO.MemoryStream]::new()
        do {
            $part = $socket.ReceiveAsync($buffer, $deadline.Token).GetAwaiter().GetResult()
            $reply.Write($buffer.Array, 0, $part.Count)
        } until ($part.EndOfMessage)
        $result = [Text.Encoding]::UTF8.GetString($reply.ToArray()) | ConvertFrom-Json -AsHashtable
        if ($result['id'] -ne 1 -or $result['error'] -or $result['result']['exceptionDetails']) { throw 'Edge test-page command failed.' }
        $result.result.result.value
    } finally { $deadline.Dispose(); $socket.Dispose() }
}

function Start-RealEdge {
    $profile = Join-Path $trial.tempDirectory ('edge-profile-' + [Guid]::NewGuid().ToString('N'))
    $page = Join-Path $trial.tempDirectory 'edge-kugou.html'
    Copy-Item -LiteralPath (Join-Path $repo 'tests\Rifflo.PlaybackFixture\edge-kugou.html') -Destination $page -Force
    $url = ([uri]$page).AbsoluteUri
    $script:edge = Register-TestProcess (Start-Process -FilePath $edgePath -ArgumentList @(
        "--user-data-dir=`"$profile`"", '--no-first-run', '--disable-background-mode', '--no-default-browser-check',
        '--autoplay-policy=no-user-gesture-required', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=0',
        "--app=$url"
    ) -PassThru)
    $activePort = Join-Path $profile 'DevToolsActivePort'
    $port = Wait-Observation 'temporary Edge DevTools port' {
        if (Test-Path -LiteralPath $activePort) { Get-Content -LiteralPath $activePort -TotalCount 1 }
    } { param($p) $p -match '^\d+$' } 20
    $pageEndpoint = Wait-Observation 'dedicated Edge tone page' {
        (Invoke-RestMethod "http://127.0.0.1:$port/json/list" -NoProxy) | Where-Object url -EQ $url | Select-Object -First 1
    } { $true } 15
    $script:edgeSocket = [uri]$pageEndpoint.webSocketDebuggerUrl
    $playing = Invoke-EdgePlayer 'document.querySelector("audio").play().then(() => !document.querySelector("audio").paused)'
    if (!$playing) { throw 'Temporary Edge audio did not start.' }
    Wait-Observation 'dedicated Edge GSMTC' { Read-RealSessions | Where-Object title -EQ $edgeTitle } {
        param($session) $session.status -eq 'Playing'
    } 20 | Out-Null
    $script:edgeIds = @(Get-EdgeTree $script:edge.Id)
}

function Set-EdgeTone([bool]$Playing) {
    if ($Playing) {
        $result = Invoke-EdgePlayer 'document.querySelector("audio").play().then(() => !document.querySelector("audio").paused)'
    } else {
        $result = Invoke-EdgePlayer '(document.querySelector("audio").pause(), document.querySelector("audio").paused)'
    }
    if (!$result) { throw 'Edge test-page playback state did not change.' }
}

function Test-LowBand([object]$Sample) {
    $Sample.bandEvidence.normalizedIndex -lt $lowDominantLimit -and $Sample.bandEvidence.peak -gt $visualFloor
}

function Test-HighBand([object]$Sample) {
    $Sample.bandEvidence.normalizedIndex -gt $highDominantLimit -and $Sample.bandEvidence.peak -gt $visualFloor
}

function Measure-Range([double[]]$Values) {
    $m = $Values | Measure-Object -Minimum -Maximum -Average
    [pscustomobject]@{ min = $m.Minimum; max = $m.Maximum; mean = $m.Average }
}

function Invoke-IsolationTrial {
    Set-RealTone 'Rifflo 200 Hz'
    $baseline = @(Read-SteadyWindow 'low-baseline')
    $oldBrowsers = @(Get-Process msedge -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    Start-RealEdge
    $edgeToken = Get-Integrity $script:edge.Id
    Add-Assertion 'temporary Edge root runs at medium integrity in the interactive session' (Test-MediumToken $edgeToken) $edgeToken
    $first = @(Read-SteadyWindow 'mixed-edge-on')
    Set-EdgeTone $false
    $off = @(Read-SteadyWindow 'mixed-edge-off')
    Set-EdgeTone $true
    $second = @(Read-SteadyWindow 'mixed-edge-on-again')
    Set-EdgeTone $false

    Set-RealTone 'Rifflo 6000 Hz'
    $positive = @(Read-SteadyWindow 'kugou-high-positive')
    $edgeOn = $first + $second
    $allLow = $baseline + $first + $off + $second
    $all = $allLow + $positive
    $endpoints = @($all.endpointId | Sort-Object -Unique)
    $baselineHigh = Measure-Range $baseline.bandEvidence.highPeak
    $mixedHigh = Measure-Range $edgeOn.bandEvidence.highPeak
    $offHigh = Measure-Range $off.bandEvidence.highPeak
    $positiveHigh = Measure-Range $positive.bandEvidence.highPeak
    $edgeLevel = Measure-Range $edgeOn.edgePeak
    $positiveLevel = Measure-Range $positive.kugouPeak
    $inputRatio = [double]::PositiveInfinity
    if ($edgeLevel.mean -gt 0 -and $positiveLevel.mean -gt 0) {
        $inputRatio = [Math]::Max($edgeLevel.mean, $positiveLevel.mean) / [Math]::Min($edgeLevel.mean, $positiveLevel.mean)
    }
    $summary = @{
        endpointIds = $endpoints; edgeRoot = $script:edge.Id; edgeTree = $script:edgeIds
        edgeAudioPids = @($edgeOn.edgeAudio.pid | Sort-Object -Unique)
        kugouAudioPids = @($all.kugouAudio.pid | Sort-Object -Unique)
        baselineHigh = $baselineHigh; mixedHigh = $mixedHigh; betweenTogglesHigh = $offHigh; positiveHigh = $positiveHigh
        mixedKugouSessionPeak = Measure-Range $edgeOn.kugouPeak
        mixedEdgeSessionPeak = $edgeLevel; positiveKugouSessionPeak = $positiveLevel
        inputRatio = if ([double]::IsFinite($inputRatio)) { $inputRatio } else { $null }
        maximumComparedHighPeak = $positiveHigh.max * $isolationRatio
        windows = @(
            foreach ($series in ,$baseline + ,$first + ,$off + ,$second + ,$positive) {
                @{ samples = $series.Count; first = $series[0].rifflo.unixTimeMs; last = $series[-1].rifflo.unixTimeMs }
            }
        )
    }
    Save-Evidence 'isolation-summary.json' $summary
    Add-Assertion 'both real players emit into the same endpoint in every Edge-on sample' (
        $endpoints.Count -eq 1 -and (Test-All $edgeOn {
            param($s) (Test-AudibleKuGouCapture $s) -and (Test-AudibleEdge $s)
        })
    ) $summary
    Add-Assertion 'browser switches on, off, and on again while KuGou keeps the same low tone and spectrum' (
        (Test-All ($baseline + $off) { param($s) $s.edgePeak -le $silenceCeiling }) -and
        (Test-All $allLow { param($s) (Test-AudibleKuGouCapture $s) -and $s.rifflo.title -ceq 'Rifflo 200 Hz' -and (Test-LowBand $s) }) -and
        (Test-All $edgeOn { param($s) (Test-AudibleEdge $s) })
    ) @{ baselineHigh = $baselineHigh; onHigh = $mixedHigh; offHigh = $offHigh }
    Add-Assertion 'at comparable session levels, mixed 6000 Hz is below 25 percent of the KuGou positive control' (
        $inputRatio -le $inputRatioLimit -and
        (Test-All $positive { param($s) (Test-AudibleKuGouCapture $s) -and (Test-HighBand $s) }) -and
        $mixedHigh.max -lt $positiveHigh.max * $isolationRatio
    ) $summary

    Set-EdgeTone $true
    Set-RealTone 'Rifflo 200 Hz followed by silence'
    $lead = @(Read-SteadyWindow 'silence-track-audible-lead' 1000 4)
    $quiet = @(Read-SteadyWindow 'kugou-playing-digital-silence' 16500)
    Add-Assertion 'a confirmed audible KuGou track enters digital silence without drawing the still-playing browser' (
        (Test-All $lead { param($s) (Test-AudibleKuGouCapture $s) -and (Test-LowBand $s) -and (Test-AudibleEdge $s) }) -and
        (Test-All $quiet {
            param($s) (Test-PlayingKuGou $s) -and $s.rifflo.title -ceq 'Rifflo 200 Hz followed by silence' -and
                (Test-AudibleEdge $s) -and $s.endpointId -eq $endpoints[0] -and $s.kugouPeak -le $silenceCeiling -and
                $s.rifflo.spectrumMode -eq 'Live' -and $s.rifflo.captureRunning -and
                $s.rifflo.spectrumVisible -and $s.bandEvidence.peak -lt 0.05
        })
    ) @{ leadSamples = $lead.Count; silenceSamples = $quiet.Count; edgePeak = Measure-Range $quiet.edgePeak }

    Set-KuGouTransport 'pause'
    $handoff = @(Read-SteadyWindow 'kugou-paused-browser-handoff')
    Add-Assertion 'pausing the real KuGou hands the caption and high-frequency spectrum to the dedicated Edge session' (
        (Test-All $handoff {
            param($s)
            $kg = $s.gsmTc | Where-Object sourceAppUserModelId -EQ 'kugou' | Select-Object -First 1
            $edgeMedia = $s.gsmTc | Where-Object title -EQ $edgeTitle | Select-Object -First 1
            $null -ne $kg -and $kg.status -eq 'Paused' -and (Test-AudibleEdge $s) -and $s.kugouPeak -le $silenceCeiling -and
                $s.rifflo.source -eq $edgeMedia.sourceAppUserModelId -and $s.rifflo.title -ceq $edgeTitle -and
                $s.rifflo.artist -ceq $edgeMedia.artist -and $s.rifflo.capturing -and
                $s.rifflo.audioMatch.targetProcessId -in $script:edgeIds -and (Test-HighBand $s)
        }) -and (Test-AdvancingCounts $handoff)
    ) @{ source = $handoff[-1].rifflo.source; match = $handoff[-1].rifflo.audioMatch }

    Stop-TestProcess $script:edge
    $survivingOldBrowsers = @(Get-Process msedge -ErrorAction SilentlyContinue | Where-Object Id -In $oldBrowsers | Select-Object -ExpandProperty Id)
    $remainingTestBrowsers = @(Get-Process msedge -ErrorAction SilentlyContinue | Where-Object Id -In $script:edgeIds | Select-Object -ExpandProperty Id)
    Save-Evidence 'edge-cleanup.json' @{
        originalPids = $oldBrowsers; originalPidsStillPresent = $survivingOldBrowsers
        temporaryPids = $script:edgeIds; temporaryPidsStillPresent = $remainingTestBrowsers
    }
    Add-Assertion 'dedicated browser process tree is closed while the pre-existing Edge window is preserved' (
        $remainingTestBrowsers.Count -eq 0 -and ($oldBrowsers.Count -eq 0 -or $survivingOldBrowsers.Count -gt 0)
    ) @{ preExistingCount = $oldBrowsers.Count; survivingCount = $survivingOldBrowsers.Count }
}

try {
    if ($Phase -eq 'Start') {
        if (Test-Path -LiteralPath $manifestPath) { throw 'Choose a new evidence directory for each run.' }
        $sdk = Join-Path $repo '.tools\dotnet\dotnet.exe'
        if (!(Test-Path -LiteralPath $sdk)) { $sdk = (Get-Command dotnet).Source }
        & $sdk build (Join-Path $repo 'tests\Rifflo.PlaybackFixture\Rifflo.PlaybackFixture.csproj') -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Playback probe build failed.' }
        $kg = Get-KuGouMain
        if (!$kg -or !$kg.Path) { throw 'Start the real KuGou application in the current session before the test.' }
        $temp = Join-Path ([IO.Path]::GetTempPath()) ('RiffloKuGou-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temp | Out-Null
        & $script:fixture --generate-kugou $temp
        if ($LASTEXITCODE -ne 0) { throw 'Tone generation failed.' }
        $script:settingsPath = Join-Path $temp 'settings.json'
        $script:diagPath = Join-Path $temp 'diagnostics.json'
        $preferredSource = if ($UseAutomaticSelection) { '' } else { 'kugou' }
        @{ preferredSource = $preferredSource; showArtist = $true; textWidth = 600 } |
            ConvertTo-Json | Set-Content -LiteralPath $script:settingsPath -Encoding utf8
        $Mode = 'Edge'
        $script:rifflo = Start-Rifflo
        $trial = [pscustomobject]@{
            tempDirectory = $temp; kugouPath = $kg.Path; releasePath = $script:exe
            releasePid = $script:rifflo.Id; releaseStartedUtc = $script:rifflo.StartTime.ToUniversalTime().ToString('O')
        }
        Save-Evidence 'run.json' $trial
        $tokens = @{ shell = Get-Integrity $PID; rifflo = Get-Integrity $script:rifflo.Id; kugou = Get-Integrity $kg.Id }
        $os = Get-CimInstance Win32_OperatingSystem
        Save-Evidence 'environment.json' @{
            os = $os.Caption; build = $os.BuildNumber; osArchitecture = $os.OSArchitecture
            releasePath = $script:exe; releaseSha256 = (Get-FileHash -LiteralPath $script:exe).Hash
            releaseDllSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $script:exe) 'Rifflo.dll')).Hash
            releaseVersion = (Get-Item -LiteralPath $script:exe).VersionInfo.ProductVersion
            kugouPath = $kg.Path; kugouVersion = (Get-Item -LiteralPath $kg.Path).VersionInfo.ProductVersion
            edgeVersion = (Get-Item -LiteralPath $edgePath).VersionInfo.ProductVersion
            preferredSource = $preferredSource; tokens = $tokens
        }
        Save-Evidence 'thresholds.json' @{
            steadySamples = $steadySamples; settleMs = $settleMs; audioPeakAtLeast = $audibleFloor
            audioSilenceAtMost = $silenceCeiling; visualPeakAbove = $visualFloor
            lowDominantBelow = $lowDominantLimit; highDominantAbove = $highDominantLimit
            highRegionStartsAt = $highRegionStart; mixedVsPositiveHighRatioBelow = $isolationRatio
            maxMeanSessionInputRatio = $inputRatioLimit; silenceLeadSeconds = 15
        }
        Add-Assertion 'Rifflo and real KuGou run at medium integrity in the interactive session' (
            (Test-MediumToken $tokens.rifflo) -and (Test-MediumToken $tokens.kugou)
        ) $tokens
        $script:ownedProcesses.Clear()
    } else {
        $trial = Read-JsonSnapshot $manifestPath
        if (!$trial) { throw 'Run the Start phase first.' }
        $script:diagPath = Join-Path $trial.tempDirectory 'diagnostics.json'
        $script:rifflo = Get-Process -Id $trial.releasePid -ErrorAction SilentlyContinue
        if ($script:rifflo -and ($script:rifflo.Path -ine $trial.releasePath -or
            $script:rifflo.StartTime.ToUniversalTime() -ne ([DateTime]$trial.releaseStartedUtc).ToUniversalTime())) {
            $script:rifflo = $null
        }
        if (!$script:rifflo -and $Phase -ne 'Finish') {
            throw 'The Rifflo process from Start is no longer alive.'
        }
        $envRecorded = Read-JsonSnapshot (Join-Path $EvidenceDirectory 'environment.json')
        if ((Get-FileHash -LiteralPath (Join-Path (Split-Path $trial.releasePath) 'Rifflo.dll')).Hash -ne $envRecorded.releaseDllSha256) {
            throw 'The release DLL changed during the run.'
        }
    }

    switch ($Phase) {
        'AutomaticSong' {
            if ($envRecorded.preferredSource) { throw 'Run Start with -UseAutomaticSelection for this phase.' }
            $track = Wait-RealTrack { param($s) $s.status -eq 'Playing' -and $s.title -and $s.artist }
            $samples = @(Read-SteadyWindow 'real-song-auto')
            Save-DynamicSong 'default automatic selection' $samples $track.title
        }
        'Songs' {
            $track = Wait-RealTrack { param($s) $s.status -eq 'Playing' -and $s.title -and $s.artist }
            $initial = @(Read-SteadyWindow 'real-song-start')
            Save-DynamicSong 'initial song' $initial $track.title
            Save-SongPicture $initial[-1]
            Set-KuGouTransport 'pause'
            $pause = @(Read-SteadyWindow 'real-song-pause' 0)
            Add-Assertion 'pause stops capture and clears the frequency bars after the pause tail' (
                $pause[-1].rifflo.source -eq 'kugou' -and $pause[-1].rifflo.state -eq 'Paused' -and
                !$pause[-1].rifflo.visible -and !$pause[-1].rifflo.capturing -and $pause[-1].bandEvidence.peak -eq 0 -and
                $pause[-1].rifflo.packetCount -eq $pause[-2].rifflo.packetCount -and
                $pause[-1].rifflo.fftCount -eq $pause[-2].rifflo.fftCount
            ) @{ levels = $pause.bandEvidence.peak; visible = $pause.rifflo.visible; statuses = $pause.rifflo.state }
            Set-KuGouTransport 'play'
            $resume = @(Read-SteadyWindow 'real-song-resume')
            Save-DynamicSong 'resumed song' $resume $track.title
            Set-KuGouTransport 'next'
            $next = Wait-RealTrack { param($s) $s.status -eq 'Playing' -and $s.title -cne $track.title -and $s.artist }
            $changed = @(Read-SteadyWindow 'real-song-next')
            Save-DynamicSong 'next song' $changed $next.title
        }
        'Exited' {
            if (Get-KuGouMain) { throw 'Use the KuGou menu to exit normally before the Exited phase.' }
            $samples = @(Read-SteadyWindow 'real-kugou-exited' 1000 3)
            Add-Assertion 'normal KuGou exit removes the GSMTC source and clears the overlay spectrum' (
                Test-All $samples {
                    param($s) $s.rifflo.source -ne 'kugou' -and !$s.rifflo.capturing -and $s.bandEvidence.peak -eq 0 -and
                        @($s.gsmTc | Where-Object sourceAppUserModelId -EQ 'kugou').Count -eq 0
                }
            ) @{ source = $samples.rifflo.source; capturing = $samples.rifflo.capturing }
        }
        'Restarted' {
            $track = Wait-RealTrack { param($s) $s.status -eq 'Playing' -and $s.title -and $s.artist }
            $old = Read-JsonSnapshot (Join-Path $EvidenceDirectory 'real-song-next.samples.json')
            $previousMatch = $old[-1].rifflo.audioMatch
            $samples = @(Read-SteadyWindow 'real-kugou-restarted')
            $match = $samples[-1].rifflo.audioMatch
            $token = Get-Integrity $match.targetProcessId
            Save-DynamicSong 'restarted KuGou song' $samples $track.title
            Add-Assertion 'same Rifflo process matches a different normally restarted medium-integrity KuGou PID' (
                $match.targetProcessId -ne $previousMatch.targetProcessId -and
                $match.targetCreatedAt -ne $previousMatch.targetCreatedAt -and (Test-MediumToken $token)
            ) @{ previous = $previousMatch; current = $match; token = $token }
        }
        'Isolation' { Invoke-IsolationTrial }
        'Finish' { Stop-TestProcess $script:rifflo }
    }
} catch {
    Save-Evidence "$($Phase.ToLowerInvariant()).error.json" @{ message = $_.Exception.Message; stack = $_.ScriptStackTrace }
    throw
} finally {
    for ($index = $script:ownedProcesses.Count - 1; $index -ge 0; $index--) {
        Stop-TestProcess $script:ownedProcesses[$index]
    }
    if ($Phase -eq 'Finish' -and $trial -and (!$script:rifflo -or $script:rifflo.HasExited)) {
        $temp = [IO.Path]::GetFullPath($trial.tempDirectory)
        $tempRoot = [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ((Split-Path $temp -Parent) -eq $tempRoot -and (Split-Path $temp -Leaf) -match '^RiffloKuGou-[a-f0-9]{32}$') {
            if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse }
            Save-Evidence 'temporary-cleanup.json' @{ removed = !(Test-Path -LiteralPath $temp); releaseExited = $true }
        }
    }
    Save-Evidence "$($Phase.ToLowerInvariant()).assertions.json" $script:assertions.ToArray()
}

if (@($script:assertions | Where-Object { !$_.passed }).Count -gt 0) { exit 1 }

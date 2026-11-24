#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x64-spectrum-verified'),
    [string]$EvidenceDirectory = (Join-Path $PSScriptRoot '..\artifacts\verification\edge-shared-tree')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $Repo 'scripts\Rifflo.Verification.Helpers.ps1')
$Mode = 'Edge'
$release = (Resolve-Path -LiteralPath $PublishDirectory).Path
$Sdk = Join-Path $Repo '.tools\dotnet\dotnet.exe'
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
Remove-Item -LiteralPath (Join-Path $EvidenceDirectory 'error.json') -ErrorAction SilentlyContinue
$script:ownedProcesses = [System.Collections.Generic.List[Diagnostics.Process]]::new()
$script:assertions = [System.Collections.Generic.List[object]]::new()
$script:tempDir = Join-Path ([IO.Path]::GetTempPath()) ('RiffloVerification-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:tempDir | Out-Null
$script:settingsPath = Join-Path $script:tempDir 'settings.json'
$script:diagPath = Join-Path $script:tempDir 'diagnostics.json'
$script:exe = Join-Path $release 'Rifflo.exe'
$script:fixture = Join-Path $Repo 'tests\Rifflo.PlaybackFixture\bin\Release\net10.0-windows10.0.26100.0\Rifflo.PlaybackFixture.exe'
$samples = [System.Collections.Generic.List[object]]::new()
$transitionSamples = [System.Collections.Generic.List[object]]::new()

function Invoke-TonePage([object]$Page, [string]$Expression) {
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
    $socket = [Net.WebSockets.ClientWebSocket]::new()
    try {
        $socket.ConnectAsync([uri]$Page.webSocketDebuggerUrl, $timeout.Token).GetAwaiter().GetResult() | Out-Null
        $command = @{ id = 1; method = 'Runtime.evaluate'; params = @{ expression = $Expression; awaitPromise = $true; returnByValue = $true } } |
            ConvertTo-Json -Depth 5 -Compress
        $bytes = [ArraySegment[byte]]::new([Text.Encoding]::UTF8.GetBytes($command))
        $socket.SendAsync($bytes, [Net.WebSockets.WebSocketMessageType]::Text, $true, $timeout.Token).GetAwaiter().GetResult() | Out-Null
        $buffer = [ArraySegment[byte]]::new([byte[]]::new(4096))
        do {
            $reply = [IO.MemoryStream]::new()
            try {
                do {
                    $part = $socket.ReceiveAsync($buffer, $timeout.Token).GetAwaiter().GetResult()
                    if ($part.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'Edge closed the page debugger.' }
                    $reply.Write($buffer.Array, 0, $part.Count)
                } until ($part.EndOfMessage)
                $message = [Text.Encoding]::UTF8.GetString($reply.ToArray()) | ConvertFrom-Json -AsHashtable
            } finally { $reply.Dispose() }
        } until ($message['id'] -eq 1)
        if ($message['error'] -or $message['result']['exceptionDetails']) { throw 'The tone fixture rejected the control command.' }
        $message['result']['result']['value']
    } finally { $socket.Dispose(); $timeout.Dispose() }
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

function Read-Phase([string]$Name, [object]$LowPage, [object]$HighPage) {
    $previous = 0L
    $settle = [Diagnostics.Stopwatch]::StartNew()
    do {
        $frame = Get-Diagnostics
        if ($frame -and $frame.unixTimeMs -gt $previous) {
            $transitionSamples.Add([pscustomobject]@{ phase = $Name; rifflo = $frame })
            $previous = $frame.unixTimeMs
        }
        Start-Sleep -Milliseconds 125
    } while ($settle.ElapsedMilliseconds -lt 2500)
    Save-Evidence 'transition-samples.json' $transitionSamples.ToArray()
    $tree = @(Get-EdgeTree $browser.Id)
    for ($index = 0; $index -lt 4; $index++) {
        $state = '(()=>{const a=document.querySelector("audio");return {paused:a.paused,time:a.currentTime,muted:a.muted,volume:a.volume,readyState:a.readyState}})()'
        $low = Invoke-TonePage $LowPage $state
        $high = if ($HighPage) { Invoke-TonePage $HighPage $state }
        $gsm = @((Get-PlaybackSessions).sessions | Where-Object { $_.title -in 'Rifflo Edge 200 Hz', 'Rifflo Edge 6000 Hz' })
        $audio = Get-AudioSessions
        $item = [pscustomobject]@{
            phase = $Name; unixTimeMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); browserTree = $tree
            lowPage = $low; highPage = $high; gsm = $gsm; endpoint = $audio.endpointId
            treeAudio = @($audio.sessions | Where-Object { $_.pid -in $tree }); rifflo = Get-Diagnostics
        }
        $samples.Add($item)
        Save-Evidence 'phase-samples.json' $samples.ToArray()
        Start-Sleep -Milliseconds 650
    }
    Write-Host "Recorded $Name"
}

try {
    if (!(Test-Path -LiteralPath $script:fixture)) {
        & $Sdk build (Join-Path $Repo 'tests\Rifflo.PlaybackFixture\Rifflo.PlaybackFixture.csproj') -c Release --nologo
        if ($LASTEXITCODE) { throw 'Unable to build the existing playback fixture.' }
    }
    & $script:fixture --generate $script:tempDir
    if ($LASTEXITCODE) { throw 'Unable to generate the existing 200 Hz and 6000 Hz fixtures.' }

    $project = Join-Path $Repo 'tests\Rifflo.BrowserTreeProbe\BrowserTreeAudioProbe.csproj'
    $probeBuild = Join-Path $script:tempDir 'browser-probe'
    $probeIntermediate = Join-Path $script:tempDir 'obj'
    & $Sdk build $project -c Release -o $probeBuild "-p:BaseIntermediateOutputPath=$probeIntermediate\" "-p:RiffloPublishDirectory=$release" --nologo
    if ($LASTEXITCODE) { throw 'Unable to build the independently metered browser tree probe.' }

    $edgePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
    $profile = Join-Path $script:tempDir 'edge-profile'
    $page = Join-Path $script:tempDir 'edge-player.html'
    Copy-Item -LiteralPath (Join-Path $Repo 'tests\Rifflo.PlaybackFixture\edge-player.html') -Destination $page
    $lowUrl = ([uri]$page).AbsoluteUri
    $highUrl = "${lowUrl}?tone=6000"
    $browserArgs = @("--user-data-dir=`"$profile`"", '--no-first-run', '--disable-background-mode', '--no-default-browser-check',
        '--autoplay-policy=no-user-gesture-required', '--remote-debugging-address=127.0.0.1', '--remote-debugging-port=0')
    $browser = Register-TestProcess (Start-Process -FilePath $edgePath -ArgumentList ($browserArgs + "--app=$lowUrl") -PassThru)
    $port = Wait-Observation 'temporary Edge DevTools port' {
        $portFile = Join-Path $profile 'DevToolsActivePort'
        if (Test-Path -LiteralPath $portFile) { Get-Content -LiteralPath $portFile -TotalCount 1 }
    } { param($p) $p -match '^\d+$' } 15
    $lowPage = Wait-Observation 'low tone in app window' {
        (Invoke-RestMethod "http://127.0.0.1:$port/json/list" -NoProxy -TimeoutSec 3) | Where-Object url -EQ $lowUrl | Select-Object -First 1
    } { $true } 10
    $selected = Wait-Observation 'low test metadata and app-window identity' {
        (Get-PlaybackSessions).sessions | Where-Object title -EQ 'Rifflo Edge 200 Hz' | Select-Object -First 1
    } { param($s) $s.status -eq 'Playing' } 15
    @{ preferredSource = $selected.sourceAppUserModelId } | ConvertTo-Json | Set-Content -LiteralPath $script:settingsPath -Encoding utf8
    $script:rifflo = Start-Rifflo
    $baseline = Wait-Diagnostics 'real Rifflo low-tone source baseline' {
        param($s) $s.processId -eq $script:rifflo.Id -and $s.source -eq $selected.sourceAppUserModelId -and $s.spectrumMode -eq 'Live' -and $s.bands[4] -gt 0.1
    } 15
    $raw = Join-Path $EvidenceDirectory 'browser-tree-raw.jsonl'
    $probeAssembly = Join-Path $probeBuild 'BrowserTreeAudioProbe.dll'
    $probeStart = @{
        FilePath = $Sdk; ArgumentList = @("`"$probeAssembly`"", $browser.Id, "`"$raw`"")
        WindowStyle = 'Hidden'; PassThru = $true; RedirectStandardError = Join-Path $EvidenceDirectory 'raw-probe-errors.log'
    }
    $probe = Register-TestProcess (Start-Process @probeStart)
    $null = Wait-Observation 'process-tree probe packets arrive' {
        if (Test-Path -LiteralPath $raw) { Get-Content -LiteralPath $raw -Tail 1 | ConvertFrom-Json }
    } { param($s) $s.packets -gt 0 -and $s.lowAmplitude -gt 0.002 } 8
    Save-Evidence 'environment.json' @{
        osVersion = [Environment]::OSVersion.VersionString; browserVersion = (Get-Item -LiteralPath $edgePath).VersionInfo.ProductVersion
        exe = $script:exe; dllHash = (Get-FileHash -LiteralPath (Join-Path $release 'Rifflo.dll')).Hash
        browserRootPid = $browser.Id; rawProbePid = $probe.Id; riffloPid = $script:rifflo.Id; selectedAumid = $selected.sourceAppUserModelId; baseline = $baseline
    }
    Read-Phase 'low-only' $lowPage $null

    Register-TestProcess (Start-Process -FilePath $edgePath -ArgumentList ($browserArgs[0], '--new-tab', $highUrl) -PassThru) | Out-Null
    $highPage = Wait-Observation 'high tone in normal Edge tab' {
        (Invoke-RestMethod "http://127.0.0.1:$port/json/list" -NoProxy -TimeoutSec 3) | Where-Object url -EQ $highUrl | Select-Object -First 1
    } { $true } 10
    Invoke-TonePage $highPage 'document.querySelector("audio").play().then(() => true)' | Out-Null
    Read-Phase 'both-playing' $lowPage $highPage

    Invoke-TonePage $highPage '(document.querySelector("audio").pause(), true)' | Out-Null
    Read-Phase 'high-paused' $lowPage $highPage

    Invoke-TonePage $highPage 'document.querySelector("audio").play().then(() => true)' | Out-Null
    Read-Phase 'both-playing-again' $lowPage $highPage

    Invoke-TonePage $lowPage '(document.querySelector("audio").pause(), true)' | Out-Null
    Read-Phase 'high-only' $lowPage $highPage

    Invoke-TonePage $lowPage 'document.querySelector("audio").play().then(() => true)' | Out-Null
    Invoke-RestMethod "http://127.0.0.1:$port/json/close/$($highPage.id)" -NoProxy -TimeoutSec 3 | Out-Null
    Wait-Observation 'competing tab closes inside the dedicated browser' {
        [pscustomobject]@{ closed = !((Invoke-RestMethod "http://127.0.0.1:$port/json/list" -NoProxy -TimeoutSec 3) | Where-Object id -EQ $highPage.id) }
    } { param($s) $s.closed } 10 | Out-Null
    Read-Phase 'competitor-closed' $lowPage $null
    $restored = Wait-Diagnostics 'same low Edge source and process return to quiet-high-band live capture' {
        param($s) $s.processId -eq $script:rifflo.Id -and $s.source -eq $selected.sourceAppUserModelId -and
            $s.spectrumMode -eq 'Live' -and $s.captureRunning -and $s.bands[4] -gt 0.1 -and $s.bands[13] -lt 0.03
    } 15
    Save-Evidence 'restored.json' $restored

    $rawSamples = @(Get-Content -LiteralPath $raw | ForEach-Object { $_ | ConvertFrom-Json })
    $summary = @(foreach ($phase in $samples | Group-Object phase) {
        $first = $phase.Group[0].unixTimeMs
        $last = $phase.Group[-1].unixTimeMs
        $rawPhase = @($rawSamples | Where-Object { $_.unixTimeMs -ge $first -and $_.unixTimeMs -le $last })
        $liveLow = @($phase.Group | Where-Object { $_.rifflo.source -eq $selected.sourceAppUserModelId -and $_.rifflo.spectrumMode -eq 'Live' })
        [pscustomobject]@{
            phase = $phase.Name; samples = $phase.Count; rawSamples = $rawPhase.Count
            rawLowMean = ($rawPhase.lowAmplitude | Measure-Object -Average).Average
            rawHighMean = ($rawPhase.highAmplitude | Measure-Object -Average).Average
            rawBothAudibleSamples = @($rawPhase | Where-Object { $_.lowAmplitude -gt 0.002 -and $_.highAmplitude -gt 0.002 }).Count
            bothPagesAdvancing = ($phase.Group[-1].lowPage['time'] -ne $phase.Group[0].lowPage['time']) -and $phase.Group[0].highPage -and
                ($phase.Group[-1].highPage['time'] -ne $phase.Group[0].highPage['time'])
            riffloTitle = @($phase.Group.rifflo.title | Sort-Object -Unique)
            riffloMode = @($phase.Group.rifflo.spectrumMode | Sort-Object -Unique)
            riffloReason = @($phase.Group.rifflo.spectrumReason | Sort-Object -Unique)
            riffloCaptureRunning = @($phase.Group.rifflo.captureRunning | Sort-Object -Unique)
            selectedLowLiveSamples = $liveLow.Count
            selectedLowLiveHighPeak = if ($liveLow.Count) { ($liveLow | ForEach-Object { $_.rifflo.bands[13] } | Measure-Object -Maximum).Maximum } else { $null }
        }
    })
    Save-Evidence 'summary.json' $summary
    $summary | Format-Table -AutoSize

    $concurrentRows = @($summary | Where-Object phase -In 'both-playing','both-playing-again')
    $concurrentSamples = @($samples | Where-Object phase -In 'both-playing','both-playing-again')
    $reportedTwoSources = @($concurrentSamples | Where-Object {
        @($_.gsm | Where-Object status -EQ 'Playing' | Select-Object -ExpandProperty sourceAppUserModelId -Unique).Count -eq 2
    }).Count -eq $concurrentSamples.Count
    $verifiedMix = @($concurrentRows | Where-Object { $_.rawBothAudibleSamples -ge 6 -and $_.bothPagesAdvancing }).Count -eq 2
    if (!$verifiedMix -or !$reportedTwoSources) {
        Save-Evidence 'environment-limit.json' @{ verifiedMix = $verifiedMix; reportedTwoSources = $reportedTwoSources }
        Write-Host 'OBSERVED this Edge run did not sustain both audible pages with distinct Windows media registrations.'
    } else {
        $unexpected = @($concurrentSamples | Where-Object {
            $_.rifflo.source -ne $selected.sourceAppUserModelId -or $_.rifflo.spectrumMode -ne 'AutomaticPreset' -or
            $_.rifflo.spectrumReason -ne 'AmbiguousSource' -or $_.rifflo.captureRunning -or
            @($_.rifflo.competingAudioSources).Count -lt 1
        })
        Add-Assertion 'two audible Edge pages sharing a process tree use automatic preset for the selected app window' ($unexpected.Count -eq 0) @{
            browserRoot = $browser.Id; activeAumids = @($concurrentSamples.gsm | Where-Object status -EQ 'Playing' | Select-Object -ExpandProperty sourceAppUserModelId -Unique)
            rawBoth = $concurrentRows.rawBothAudibleSamples; rows = $concurrentSamples.Count; unexpected = $unexpected.Count
        }
        $foreignWhileLive = @($transitionSamples | Where-Object {
            $_.phase -in 'both-playing', 'both-playing-again' -and $_.rifflo.source -eq $selected.sourceAppUserModelId -and
            $_.rifflo.spectrumMode -eq 'Live' -and $_.rifflo.bands[13] -gt 0.15
        })
        Add-Assertion 'no sampled transition labels the competing 6000 Hz peak as selected live audio' ($foreignWhileLive.Count -eq 0) $foreignWhileLive
        Add-Assertion 'closing the competing tab resumes the same app window live without restarting Rifflo' (
            $restored.processId -eq $baseline.processId -and $restored.source -eq $baseline.source -and $restored.title -eq $baseline.title -and
            @($restored.competingAudioSources).Count -eq 0
        ) @{ source = $restored.source; processId = $restored.processId; low = $restored.bands[4]; high = $restored.bands[13] }
    }
    if (@($script:assertions | Where-Object { !$_.passed }).Count) { throw 'Edge shared-tree isolation failed.' }
} catch {
    Save-Evidence 'error.json' @{ message = $_.Exception.Message; scriptStackTrace = $_.ScriptStackTrace }
    throw
} finally {
    for ($index = $script:ownedProcesses.Count - 1; $index -ge 0; $index--) { Stop-TestProcess $script:ownedProcesses[$index] }
    Save-Evidence 'assertions.json' $script:assertions.ToArray()
    $resolved = [IO.Path]::GetFullPath($script:tempDir)
    if ((Split-Path -Parent $resolved) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        (Split-Path -Leaf $resolved) -like 'RiffloVerification-*') {
        Remove-Item -LiteralPath $resolved -Recurse -ErrorAction SilentlyContinue
    }
}

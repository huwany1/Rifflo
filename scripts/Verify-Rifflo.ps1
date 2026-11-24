#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Fixtures', 'Functional', 'Performance', 'Edge', 'EdgeSharedTree', 'UI', 'Overlay', 'SessionChurn', 'Spectrum', 'Broadcast')]
    [string]$Mode = 'Functional',
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x64-spectrum-verified'),
    [string]$EvidenceDirectory,
    [ValidateRange(60, 1800)]
    [int]$PhaseSeconds = 62,
    [switch]$KeepTemporaryFiles,
    [switch]$CrossMonitor,
    [ValidateRange(8, 60)]
    [int]$SourceCycleCount = 24
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($Mode -eq 'EdgeSharedTree') {
    $arguments = @{ PublishDirectory = $PublishDirectory }
    if ($EvidenceDirectory) { $arguments.EvidenceDirectory = $EvidenceDirectory }
    & (Join-Path $PSScriptRoot 'Verify-Rifflo-EdgeSharedTree.ps1') @arguments
    exit 0
}

. (Join-Path $PSScriptRoot 'Rifflo.Verification.Helpers.ps1')
if ($Mode -in 'Overlay', 'Spectrum', 'Broadcast') { . (Join-Path $PSScriptRoot 'Rifflo.Verification.Appearance.ps1') }
if ($Mode -in 'UI', 'Broadcast') { . (Join-Path $PSScriptRoot 'Rifflo.Verification.Placement.ps1') }
if ($Mode -eq 'Spectrum') { . (Join-Path $PSScriptRoot 'Rifflo.Verification.Spectrum.ps1') }
if ($Mode -eq 'Broadcast') { . (Join-Path $PSScriptRoot 'Rifflo.Verification.Broadcast.ps1') }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (!$EvidenceDirectory) { $EvidenceDirectory = Join-Path $repo ("artifacts\verification\" + $Mode.ToLowerInvariant()) }
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
Remove-Item -LiteralPath (Join-Path $EvidenceDirectory 'error.json') -ErrorAction SilentlyContinue
$script:ownedProcesses = [System.Collections.Generic.List[Diagnostics.Process]]::new()
$script:assertions = [System.Collections.Generic.List[object]]::new()
$script:tempDir = Join-Path ([IO.Path]::GetTempPath()) ('RiffloVerification-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:tempDir | Out-Null
$settingsName = 'check-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$script:settingsPath = Join-Path $script:tempDir "$settingsName.json"
$script:diagPath = Join-Path $script:tempDir 'diagnostics.json'
$script:exe = [IO.Path]::GetFullPath((Join-Path $PublishDirectory 'Rifflo.exe'))
$script:rifflo = $null
$exitCode = 0

function Get-BandEvidence([object]$Sample) {
    $bands = @($Sample.bands)
    if ($bands.Count -lt 2) { return [pscustomobject]@{ dominant = 0; normalizedIndex = 0; peak = 0; highPeak = 0 } }
    $dominant = 0
    $highPeak = 0.0
    for ($index = 0; $index -lt $bands.Count; $index++) {
        if ($bands[$index] -gt $bands[$dominant]) { $dominant = $index }
        if ($index -ge [int]($bands.Count * 0.64)) { $highPeak = [math]::Max($highPeak, $bands[$index]) }
    }
    [pscustomobject]@{ dominant = $dominant; normalizedIndex = $dominant / ($bands.Count - 1); peak = $bands[$dominant]; highPeak = $highPeak }
}

function Get-ProductWindowRoot {
    $settings = Wait-Observation 'Rifflo settings window' { Find-RiffloWindow $script:rifflo 'Rifflo 设置' } { $true }
    [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$settings.Hwnd)
}

function Set-PreferredFixture([string]$Source) {
    Invoke-RiffloTray '设置'
    $settings = Get-ProductWindowRoot
    $options = Get-SettingControl $settings 'SettingsOptions'
    $scroll = $options.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 100) }
    Select-SettingOption $settings 'PreferredSource' $Source
    $saved = Wait-Observation 'preferred player written through settings UI' { Read-JsonSnapshot $script:settingsPath } { param($s) $s.preferredSource -eq $Source } 5
    Save-Evidence 'preferred-source.json' $saved
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
}

function Get-ControlledAudio([int[]]$ProcessIds) {
    $all = Get-AudioSessions
    [pscustomobject]@{ endpointId = $all.endpointId; sessions = @($all.sessions | Where-Object pid -In $ProcessIds) }
}

function Measure-Isolation([object]$Target, [object]$Disturbance) {
    $series = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt 11; $index++) {
        $diagnostics = Get-Diagnostics
        $series.Add([pscustomobject]@{
            utc = [DateTimeOffset]::UtcNow.ToString('O')
            audio = Get-ControlledAudio -ProcessIds @($Target.process.Id, $Disturbance.process.Id)
            diagnostics = $diagnostics
            bands = Get-BandEvidence $diagnostics
        })
        Start-Sleep -Milliseconds 920
    }
    return $series.ToArray()
}

function Invoke-Performance {
    $script:rifflo = Start-Rifflo
    Wait-Diagnostics 'initial idle' { param($s) !$s.visible -and !$s.capturing } | Out-Null
    Start-Sleep -Seconds 3
    $idle = Measure-RiffloPhase $script:rifflo 'idle' $PhaseSeconds
    Add-Assertion 'idle does not generate packets, FFT or renders' ($idle.packets -eq 0 -and $idle.fft -eq 0 -and $idle.draw -eq 0) $idle.phase

    $targetPlayer = Start-PlaybackFixture 'Target'
    Set-Playback $targetPlayer 'Rifflo performance 200 Hz' 'low.wav'
    Wait-Diagnostics 'live 200 Hz spectrum' { param($s) $s.title -eq 'Rifflo performance 200 Hz' -and $s.visible -and $s.capturing -and $s.fftCount -gt 0 } | Out-Null
    Start-Sleep -Seconds 5
    $playing = Measure-RiffloPhase $script:rifflo 'playing-visible' $PhaseSeconds
    Add-Assertion 'visible playback generates packets, FFT and renders' ($playing.packets -gt 0 -and $playing.fft -gt 0 -and $playing.draw -gt 0) $playing.phase

    Invoke-RiffloTray '隐藏悬浮窗'
    Wait-Diagnostics 'manually hidden playback' { param($s) $s.manuallyHidden -and !$s.visible -and !$s.capturing } | Out-Null
    Start-Sleep -Seconds 3
    $hidden = Measure-RiffloPhase $script:rifflo 'playing-hidden' $PhaseSeconds
    Add-Assertion 'manually hidden playback stops packets, FFT and renders' ($hidden.packets -eq 0 -and $hidden.fft -eq 0 -and $hidden.draw -eq 0) $hidden.phase
    Add-Assertion 'source stays playing in all hidden samples' ($hidden.sampledStates.Count -eq 1 -and $hidden.sampledStates[0] -eq 'Playing' -and (Read-JsonSnapshot $targetPlayer.status).playbackState -eq 'Playing') $hidden.phase
    Add-Assertion 'all three elapsed durations are at least 60 seconds' ($idle.elapsedSeconds -ge 60 -and $playing.elapsedSeconds -ge 60 -and $hidden.elapsedSeconds -ge 60) @($idle.elapsedSeconds, $playing.elapsedSeconds, $hidden.elapsedSeconds)
    try { Invoke-RiffloTray '退出' } catch { Write-Warning $_.Exception.Message }
}

function Invoke-Edge {
    $edgePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
    $profile = Join-Path $script:tempDir 'edge-profile'
    $html = Join-Path $script:tempDir 'edge-player.html'
    Copy-Item -LiteralPath (Join-Path $repo 'tests\Rifflo.PlaybackFixture\edge-player.html') -Destination $html
    $url = ([uri]$html).AbsoluteUri
    $browser = Register-TestProcess (Start-Process -FilePath $edgePath -ArgumentList @("--user-data-dir=`"$profile`"", '--no-first-run', '--disable-background-mode', '--no-default-browser-check', '--autoplay-policy=no-user-gesture-required', "--app=$url") -PassThru)
    $session = Wait-Observation 'dedicated Edge GSMTC session' {
        (Get-PlaybackSessions).sessions | Where-Object title -EQ 'Rifflo Edge 200 Hz' | Select-Object -First 1
    } { param($s) $s.status -eq 'Playing' } 20
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'")
    $treeIds = [System.Collections.Generic.List[int]]::new()
    $treeIds.Add($browser.Id)
    do {
        $count = $treeIds.Count
        foreach ($process in $processes) {
            if ($process.ParentProcessId -in $treeIds -and $process.ProcessId -notin $treeIds) { $treeIds.Add($process.ProcessId) }
        }
    } while ($treeIds.Count -ne $count)
    $window = (& $script:fixture --window-aumid $browser.Id | ConvertFrom-Json)
    $audio = Get-ControlledAudio -ProcessIds $treeIds.ToArray()
    $script:rifflo = Start-Rifflo
    $observed = Wait-Diagnostics 'Edge automated metadata selection' { param($s) $s.title -eq 'Rifflo Edge 200 Hz' -and $s.visible }
    Start-Sleep -Seconds 3
    $latest = Get-Diagnostics
    $bands = Get-BandEvidence $latest
    Save-Evidence 'edge.json' ([pscustomobject]@{ browserVersion = (Get-Item $edgePath).VersionInfo.ProductVersion; gsmTc = $session; window = $window; processTree = $treeIds.ToArray(); coreAudio = $audio; first = $observed; later = $latest; bandEvidence = $bands })
    Add-Assertion 'Edge published playing metadata' ($session.sourceAppUserModelId -eq $window.aumid -and $session.status -eq 'Playing') $session
    Add-Assertion 'audible Edge tree child is distinct from the browser main PID' (@($audio.sessions | Where-Object { $_.pid -ne $browser.Id -and $_.peak -gt 0 }).Count -gt 0) $audio
    Add-Assertion 'release overlay streams an audible Edge tree spectrum' ($latest.title -eq $session.title -and $latest.source -eq $session.sourceAppUserModelId -and $latest.capturing -and $latest.fftCount -gt 0 -and $bands.normalizedIndex -lt 0.45 -and $bands.peak -gt 0) $bands

    $sameSource = $false
    $observations = [System.Collections.Generic.List[object]]::new()
    foreach ($tab in @(@{ url = "${url}?tone=6000"; phase = 'one normal tab and the app window' }, @{ url = $url; phase = 'two normal tabs' })) {
        Register-TestProcess (Start-Process -FilePath $edgePath -ArgumentList @("--user-data-dir=`"$profile`"", '--new-tab', $tab.url) -PassThru) | Out-Null
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        do {
            $sessions = @((Get-PlaybackSessions).sessions | Where-Object { $_.title -in 'Rifflo Edge 200 Hz', 'Rifflo Edge 6000 Hz' })
            $observations.Add([pscustomobject]@{ phase = $tab.phase; elapsedMs = $deadline.ElapsedMilliseconds; sessions = $sessions })
            $sameSource = @($sessions | Where-Object status -EQ 'Playing' | Group-Object sourceAppUserModelId | Where-Object {
                @($_.Group | Select-Object -Unique -ExpandProperty title).Count -eq 2
            }).Count -gt 0
            if (!$sameSource) { Start-Sleep -Milliseconds 550 }
        } while (!$sameSource -and $deadline.ElapsedMilliseconds -lt 6500)
        if ($sameSource) { break }
    }
    Save-Evidence 'edge-two-page-registration.json' @{ sameSourcePlayingReported = $sameSource; observations = $observations.ToArray() }
    if ($sameSource) {
        $ambiguous = Wait-Diagnostics 'both Edge pages publish the same active media identity' {
            param($s) $s.title -in 'Rifflo Edge 200 Hz', 'Rifflo Edge 6000 Hz' -and $s.spectrumMode -eq 'AutomaticPreset' -and $s.spectrumReason -eq 'AmbiguousSource'
        }
        Add-Assertion 'simultaneously registered same-source Edge pages trigger automatic preset' (!$ambiguous.captureRunning -and $ambiguous.spectrumVisible) $ambiguous
    } else {
        Write-Host 'OBSERVED Edge did not register both playing pages with the same Windows media identity during this run.'
    }
}

function Invoke-Fixtures {
    $player = Start-PlaybackFixture 'Target'
    $other = Start-PlaybackFixture 'Interference'
    Set-Playback $player 'Rifflo Fixture 200 Hz' 'low.wav'
    Set-Playback $other 'Rifflo Fixture Intermittent 6000 Hz' 'intermittent-high.wav'
    $sm = Wait-Observation 'two system media sessions' {
        $gsm = Get-PlaybackSessions
        @($gsm.sessions | Where-Object sourceAppUserModelId -like 'Rifflo.Verification.*')
    } { param($rows) @($rows).Count -eq 2 }
    $observations = @(for ($n = 0; $n -lt 10; $n++) {
        Get-ControlledAudio -ProcessIds @($player.process.Id, $other.process.Id)
        Start-Sleep -Milliseconds 800
    })
    $windowId1 = (& $script:fixture --window-aumid $player.process.Id | ConvertFrom-Json)
    $windowId2 = (& $script:fixture --window-aumid $other.process.Id | ConvertFrom-Json)
    Save-Evidence 'fixture.json' ([pscustomobject]@{ sessions = $sm; samples = $observations; target = $windowId1; interference = $windowId2 })
    Add-Assertion 'distinct GSMTC and window identities associate with distinct PIDs' ($player.process.Id -ne $other.process.Id -and $windowId1.aumid -eq $player.aumid -and $windowId2.aumid -eq $other.aumid) @($windowId1, $windowId2)
    $peaks = $observations.sessions | Where-Object pid -EQ $other.process.Id | Select-Object -ExpandProperty peak
    Add-Assertion 'independent endpoint meter shows high-frequency activity and silence' (($peaks | Measure-Object -Maximum).Maximum -gt 0.01 -and ($peaks | Measure-Object -Minimum).Minimum -lt 0.001) @($peaks)
}

function Measure-Visibility([string]$Step, [int]$Milliseconds) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $window = Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗' -IncludeHidden
        $diagnostics = Get-Diagnostics
        [pscustomobject]@{ step = $Step; elapsedMs = $timer.ElapsedMilliseconds; visible = $null -ne $window -and $window.Visible; source = $diagnostics.source; capturing = $diagnostics.capturing }
        Start-Sleep -Milliseconds 28
    } while ($timer.ElapsedMilliseconds -lt $Milliseconds)
}

function Test-ForeignSessionVisibility([object]$Target) {
    $extra = Start-PlaybackFixture 'Unrelated'
    Set-Playback $extra 'Rifflo independent temporary session' 'high.wav' -NoWait
    $appeared = @(Measure-Visibility 'unrelated session starts' 2500)
    $extraState = Read-JsonSnapshot $extra.status
    Set-Playback $extra 'Rifflo independent temporary session' 'high.wav' 'exit' -NoWait
    $disappeared = @(Measure-Visibility 'unrelated session exits' 2500)
    $samples = $appeared + $disappeared
    $misses = @($samples | Where-Object { !$_.visible -or $_.source -ne $Target.aumid })
    Save-Evidence 'foreign-session-visibility.json' ([pscustomobject]@{ count = $samples.Count; misses = $misses; addedPlayerState = $extraState; samples = $samples })
    Add-Assertion 'preferred live overlay remains visible across unrelated session addition and removal' ($null -ne $extraState -and $extraState.playbackState -eq 'Playing' -and $extra.process.HasExited -and $misses.Count -eq 0) @{ samples = $samples.Count; visibilityOrSourceMisses = $misses.Count; extraPid = $extra.process.Id }
}

function Invoke-SessionChurn {
    $player = Start-PlaybackFixture 'Target'
    Set-Playback $player 'Rifflo session stability 200 Hz' 'low.wav'
    $script:rifflo = Start-Rifflo
    Wait-Diagnostics 'foreground-independent target spectrum' { param($s) $s.source -eq $player.aumid -and $s.visible -and $s.capturing } | Out-Null
    Set-PreferredFixture $player.aumid
    Test-ForeignSessionVisibility $player
}

function Invoke-Functional {
    $player = Start-PlaybackFixture 'Target'
    Set-Playback $player 'Rifflo release 200 Hz initial' 'low.wav'
    $script:rifflo = Start-Rifflo
    $start = Wait-Diagnostics 'active track read proactively at launch' { param($s) $s.title -eq 'Rifflo release 200 Hz initial' -and $s.visible -and $s.capturing }
    Add-Assertion 'published EXE proactively reads the already-playing track' ($start.source -eq $player.aumid -and -not $start.preview) $start

    $pauseTimer = [Diagnostics.Stopwatch]::StartNew()
    Set-Playback $player 'Rifflo release 200 Hz initial' 'low.wav' 'paused'
    $pauseAcknowledgedMs = $pauseTimer.ElapsedMilliseconds
    $atPause = Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗'
    do {
        Start-Sleep -Milliseconds 35
        $visible = Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗'
    } while ($null -ne $visible -and $pauseTimer.ElapsedMilliseconds -lt 4500)
    $pauseMs = $pauseTimer.ElapsedMilliseconds
    $paused = Wait-Diagnostics 'paused after two-second tail' { param($s) $s.state -eq 'Paused' -and !$s.visible -and !$s.capturing }
    Add-Assertion 'pause keeps the actual overlay briefly then hides near 2 seconds' ($null -ne $atPause -and $null -eq $visible -and $pauseMs -ge 1650 -and $pauseMs -le 2800) @{ commandToHideMs = $pauseMs; commandToFixtureAcknowledgementMs = $pauseAcknowledgedMs; diagnostics = $paused }
    Set-Playback $player 'Rifflo release 200 Hz resumed' 'low.wav'
    $resumed = Wait-Diagnostics 'release resume' { param($s) $s.title -eq 'Rifflo release 200 Hz resumed' -and $s.visible -and $s.capturing }

    foreach ($index in 1..5) {
        Set-Playback $player "Rifflo rapid track $index" 'low.wav'
    }
    $final = Wait-Diagnostics 'last rapid track' { param($s) $s.title -eq 'Rifflo rapid track 5' -and $s.source -eq $player.aumid }
    Start-Sleep -Milliseconds 2200
    Add-Assertion 'latest metadata wins after five rapid updates' ((Get-Diagnostics).title -eq $final.title) @{ initial = $resumed; latest = $final }

    $other = Start-PlaybackFixture 'Interference'
    Set-Playback $other 'Rifflo disturbance 6000 Hz' 'intermittent-high.wav'
    Wait-Observation 'two playing fixtures and an eligible current GSMTC session' { Get-PlaybackSessions } {
        param($gsm) $gsm.current -in @($player.aumid, $other.aumid) -and
            @($gsm.sessions | Where-Object { $_.sourceAppUserModelId -in @($player.aumid, $other.aumid) -and $_.status -eq 'Playing' }).Count -eq 2
    } | Out-Null
    $autoObservations = @(for ($sampleIndex = 0; $sampleIndex -lt 8; $sampleIndex++) {
        Start-Sleep -Milliseconds 900
        $current = Get-PlaybackSessions
        [pscustomobject]@{
            systemCurrent = $current.current
            controlledSessions = @($current.sessions | Where-Object sourceAppUserModelId -Like 'Rifflo.Verification.*')
            rifflo = Get-Diagnostics
        }
    })
    Save-Evidence 'automatic-selection.json' $autoObservations
    $currentWithBothPlaying = @($autoObservations | Where-Object { $_.systemCurrent -eq $other.aumid -and @($_.controlledSessions | Where-Object status -EQ 'Playing').Count -eq 2 })
    $consistent = @($currentWithBothPlaying | Select-Object -Skip 1 | Where-Object { $_.rifflo.source -ne $_.systemCurrent })
    Add-Assertion 'automatic mode follows current GSMTC while both fixtures play' ($currentWithBothPlaying.Count -ge 3 -and $consistent.Count -eq 0) @{ observedBothPlaying = $currentWithBothPlaying.Count; mismatchesPastOneSnapshot = $consistent.Count }

    Set-PreferredFixture $player.aumid
    $preferred = Wait-Diagnostics 'user preferred target while disturbance keeps playing' { param($s) $s.source -eq $player.aumid -and $s.capturing }
    $isolation = @(Measure-Isolation $player $other)
    Save-Evidence 'source-isolation.samples.json' $isolation
    $highPeaks = @($isolation.audio.sessions | Where-Object pid -EQ $other.process.Id | Select-Object -ExpandProperty peak)
    $peakStats = $highPeaks | Measure-Object -Minimum -Maximum
    Add-Assertion 'preferred player remains selected under external activity' (@($isolation | Where-Object { $_.diagnostics.source -ne $player.aumid -or !$_.diagnostics.capturing }).Count -eq 0) $preferred.source
    Add-Assertion 'external high-frequency player audibly cycles on and off' ($peakStats.Maximum -gt 0.01 -and $peakStats.Minimum -lt 0.001) $highPeaks

    Set-Playback $other 'Rifflo disturbance stopped' 'high.wav' 'stopped'
    Set-Playback $player 'Rifflo same-source 6000 Hz positive control' 'high.wav'
    $positive = Wait-Diagnostics 'same-source high-frequency spectrum' {
        param($s) $e = Get-BandEvidence $s; $s.title -eq 'Rifflo same-source 6000 Hz positive control' -and $e.normalizedIndex -gt 0.65 -and $e.peak -gt 0.01
    }
    $high = Get-BandEvidence $positive
    $isolatedHighMaximum = ($isolation.bands.highPeak | Measure-Object -Maximum).Maximum
    $isolatedLowDominants = @($isolation | Where-Object { $_.bands.normalizedIndex -lt 0.45 -and $_.bands.peak -gt 0.01 }).Count
    Save-Evidence 'positive-control.json' ([pscustomobject]@{ diagnostics = $positive; peak = $high; isolatedWindowHighMaximum = $isolatedHighMaximum; lowDominantSamples = $isolatedLowDominants })
    Add-Assertion 'process isolation rejects external 6000 Hz but detects it when played by target PID' ($isolatedLowDominants -eq $isolation.Count -and $isolatedHighMaximum -lt $high.peak * 0.25) @{ externalHighMax = $isolatedHighMaximum; sameProcessHighPeak = $high.peak; stableLowSamples = $isolatedLowDominants }

    $bounds = (Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗').Bounds
    $controlSurface = Start-ClickSurface (Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗') 'dark' (Join-Path $script:tempDir 'positive-control-clicks.txt')
    Wait-Observation 'background window for spectrum pictures' { Find-RiffloWindow $controlSurface 'Rifflo verification click surface' } { $true } | Out-Null
    Set-Playback $player 'Rifflo same-process intermittent 6000 Hz' 'intermittent-high.wav'
    $strong = Wait-Diagnostics 'audible segment of same-process positive control' { param($s) $s.title -eq 'Rifflo same-process intermittent 6000 Hz' -and (Get-BandEvidence $s).highPeak -gt 0.1 }
    Save-ScreenCrop 'same-process-6khz-audible.png' ($bounds.Left-8) ($bounds.Top-8) ($bounds.Right-$bounds.Left+16) ($bounds.Bottom-$bounds.Top+16)
    $quiet = Wait-Diagnostics 'silent segment of same-process positive control' { param($s) $s.title -eq 'Rifflo same-process intermittent 6000 Hz' -and (Get-BandEvidence $s).peak -lt 0.05 }
    Save-ScreenCrop 'same-process-6khz-quiet.png' ($bounds.Left-8) ($bounds.Top-8) ($bounds.Right-$bounds.Left+16) ($bounds.Bottom-$bounds.Top+16)
    Stop-TestProcess $controlSurface
    Save-Evidence 'same-process-dynamics.json' ([pscustomobject]@{ processId = $player.process.Id; audible = $strong; quiet = $quiet; audibleBand = Get-BandEvidence $strong; quietBand = Get-BandEvidence $quiet })
    Add-Assertion 'visible spectrum responds to the same process changing from 6000 Hz to silence' ($strong.source -eq $quiet.source -and $strong.visible -and $quiet.visible -and $quiet.drawCount -gt $strong.drawCount) @{ loud = (Get-BandEvidence $strong).peak; quiet = (Get-BandEvidence $quiet).peak; drawDelta = $quiet.drawCount - $strong.drawCount }

    Set-Playback $player 'Rifflo hidden original' 'low.wav'
    Invoke-RiffloTray '隐藏悬浮窗'
    $hidden = Wait-Diagnostics 'manual hide' { param($s) $s.manuallyHidden -and !$s.visible -and !$s.capturing }
    Set-Playback $player 'Rifflo hidden changed title' 'low.wav'
    $changed = Wait-Diagnostics 'new metadata while hidden' { param($s) $s.title -eq 'Rifflo hidden changed title' }
    Add-Assertion 'manual hiding survives fresh playing metadata' ($changed.manuallyHidden -and !$changed.visible -and !$changed.capturing -and $changed.fftCount -eq $hidden.fftCount) $changed
    Invoke-RiffloTray '恢复悬浮窗'
    Wait-Diagnostics 'manually restored spectrum' { param($s) $s.visible -and $s.capturing -and !$s.manuallyHidden } | Out-Null
    Test-ForeignSessionVisibility $player

    $before = Get-ProcessSample $script:rifflo
    $cycles = @(foreach ($cycle in 1..$SourceCycleCount) {
        Set-Playback $other "Rifflo source cycle $cycle high" 'high.wav'
        Set-Playback $player "Rifflo source cycle $cycle pause" 'low.wav' 'paused'
        $secondSource = Wait-Diagnostics "cycle $cycle alternative player" { param($s) $s.source -eq $other.aumid -and $s.title -eq "Rifflo source cycle $cycle high" -and $s.capturing }
        Set-Playback $player "Rifflo source cycle $cycle low" 'low.wav'
        $firstSource = Wait-Diagnostics "cycle $cycle returned preferred player" { param($s) $s.source -eq $player.aumid -and $s.title -eq "Rifflo source cycle $cycle low" -and $s.capturing }
        [pscustomobject]@{ cycle = $cycle; alternate = $secondSource; preferred = $firstSource; process = Get-ProcessSample $script:rifflo }
    })
    Set-Playback $other 'Rifflo disturbance stopped' 'high.wav' 'stopped'
    Start-Sleep -Seconds 3
    $after = Get-ProcessSample $script:rifflo
    Set-Playback $player 'Rifflo cycles settle while idle' 'low.wav' 'paused'
    Wait-Diagnostics 'cycle observation settles while idle' { param($s) !$s.capturing -and !$s.visible } | Out-Null
    Start-Sleep -Seconds 20
    $settled = Get-ProcessSample $script:rifflo
    Save-Evidence 'source-cycles.json' ([pscustomobject]@{ before = $before; cycles = $cycles; after = $after; settledAfterIdle = $settled })
    Add-Assertion "$SourceCycleCount cycles switch source while preferred player pauses and return on resume" ($cycles.Count -eq $SourceCycleCount) @{ startThreadCount = $before.threadCount; endThreadCount = $after.threadCount; startHandleCount = $before.handleCount; endHandleCount = $after.handleCount; settledThreadCount = $settled.threadCount; settledHandleCount = $settled.handleCount }

    $oldId = $player.process.Id
    Stop-TestProcess $player.process
    Wait-Diagnostics 'target exit observed' { param($s) !$s.capturing -and !$s.visible } | Out-Null
    $replacement = Start-PlaybackFixture 'Target'
    Set-Playback $replacement 'Rifflo restarted with new PID' 'low.wav'
    $afterRestart = Wait-Diagnostics 'capture recovered after player restart with new PID' { param($s) $s.title -eq 'Rifflo restarted with new PID' -and $s.capturing -and $s.source -eq $replacement.aumid }
    $newAudio = Get-ControlledAudio -ProcessIds @($oldId,$replacement.process.Id)
    Save-Evidence 'audio-restart.json' ([pscustomobject]@{ oldPid = $oldId; newPid = $replacement.process.Id; audio = $newAudio; diagnostic = $afterRestart })
    Add-Assertion 'process replacement reacquires the same source through a new audible PID' ($oldId -ne $replacement.process.Id -and @($newAudio.sessions | Where-Object { $_.pid -eq $replacement.process.Id -and $_.peak -gt 0.01 }).Count -gt 0) $newAudio
}

function Start-ClickSurface([object]$Overlay, [string]$Shade, [string]$ClickLog) {
    $b = $Overlay.Bounds
    $x = [math]::Max(0, $b.Left - 20)
    $y = $b.Top - 24
    $width = $b.Right - $x + 24
    $height = $b.Bottom - $y + 20
    Register-TestProcess (Start-Process -FilePath $script:fixture -ArgumentList @('--surface', $x, $y, $width, $height, $Shade, "`"$ClickLog`"") -WindowStyle Hidden -PassThru)
}

function Read-OverlayPixels([string]$File, [int]$ScreenLeft) {
    $image = [System.Drawing.Bitmap]::new((Join-Path $EvidenceDirectory $File))
    try {
        $blueLeft = $image.Width
        $blueCount = 0
        $sameAsBackground = 0
        $margin = $image.GetPixel(2, 2)
        for ($y = 8; $y -lt $image.Height - 8; $y++) {
            for ($x = 8; $x -lt $image.Width - 8; $x++) {
                $pixel = $image.GetPixel($x, $y)
                if ([int]$pixel.B - [int]$pixel.R -gt 45 -and [int]$pixel.G - [int]$pixel.R -gt 26) {
                    $blueCount++
                    $blueLeft = [math]::Min($blueLeft, $x)
                }
                if ($pixel.ToArgb() -eq $margin.ToArgb()) { $sameAsBackground++ }
            }
        }
        [pscustomobject]@{
            spectrumPixels = $blueCount
            spectrumLeft = $ScreenLeft + $blueLeft
            backgroundRgb = @($margin.R, $margin.G, $margin.B)
            matchingBackgroundFraction = $sameAsBackground / (($image.Width - 16) * ($image.Height - 16))
        }
    } finally { $image.Dispose() }
}

function Test-ScaledSettings([object]$Settings) {
    $monitors = @([RiffloVerificationNative]::Monitors())
    $monitor = $monitors | Sort-Object Primary | Select-Object -First 1
    $work = $monitor.Work
    $transform = $Settings.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    $transform.Move(($work.Left + 80), ($work.Top + 80))
    $window = Wait-Observation 'settings window enters the target scaled work area' {
        Find-RiffloWindow $script:rifflo 'Rifflo 设置'
    } { param($w) $w.Bounds.Left -ge $work.Left -and $w.Bounds.Left -lt $work.Right -and $w.Dpi -gt 0 }
    $scale = $window.Dpi / 96.0
    $height = [math]::Min(385 * $scale, $work.Bottom - $work.Top - 100)
    $width = [math]::Min(485 * $scale, $work.Right - $work.Left - 100)
    $transform.Resize($width, $height)
    $window = Wait-Observation 'settings has a compact test viewport' { Find-RiffloWindow $script:rifflo 'Rifflo 设置' } {
        param($w) ($w.Bounds.Bottom - $w.Bounds.Top) * 96.0 / $w.Dpi -lt 490 -and $w.Bounds.Bottom -le $work.Bottom
    } 5
    $scroll = (Get-SettingControl $Settings 'SettingsOptions').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $scroll.SetScrollPercent(-1, 0)
    $controls = @('Layout', 'TextWidth', 'ScrollMode')
    $visible = @($controls | Where-Object { !(Get-SettingControl $Settings $_).Current.IsOffscreen })
    Set-SettingNumber $Settings 'TextWidth' 280
    Wait-Diagnostics 'text width from compact settings is applied' {
        param($s) $s.preview -and [math]::Abs($s.overlayLayout.caption.width - 280) -le 0.5
    } | Out-Null
    Set-SettingNumber $Settings 'TextWidth' 260
    $b = $window.Bounds
    Save-ScreenCrop 'settings-scaled-top.png' $b.Left $b.Top ($b.Right - $b.Left) ($b.Bottom - $b.Top)
    $scroll.SetScrollPercent(-1, 100)
    $lockVisible = !(Get-SettingControl $Settings 'PositionLocked').Current.IsOffscreen
    $resetVisible = !(Get-SettingControl $Settings 'DefaultPosition').Current.IsOffscreen
    Save-ScreenCrop 'settings-scaled-bottom.png' $b.Left $b.Top ($b.Right - $b.Left) ($b.Bottom - $b.Top)
    Add-Assertion 'compact settings expose and apply the new controls on a scaled display' `
        ($visible.Count -eq $controls.Count -and $lockVisible -and $resetVisible) `
        @{ dpi = $window.Dpi; bounds = $b; topControls = $visible; lockVisible = $lockVisible; resetVisible = $resetVisible }
}

function Invoke-DesktopVerification {
    $script:rifflo = Start-Rifflo
    $idle = Wait-Diagnostics 'without actively playing media' { param($s) !$s.visible -and !$s.preview -and !$s.capturing }
    Add-Assertion 'idle launch uses tray without overlay or preview' ($null -eq (Find-RiffloWindow $script:rifflo 'Rifflo 设置')) $idle
    $secondary = Start-Rifflo
    $settings = Get-ProductWindowRoot
    $preview = Wait-Diagnostics 'independent settings preview while idle' { param($s) !$s.visible -and $s.preview -and !$s.capturing }
    $oneInstance = $secondary.WaitForExit(5000) -and !$script:rifflo.HasExited -and $null -ne (Find-RiffloWindow $script:rifflo 'Rifflo 外观预览')
    Add-Assertion 'second launch with the same test profile activates the existing settings and preview' $oneInstance `
        @{ firstPid = $script:rifflo.Id; secondPid = $secondary.Id; preview = $preview.preview }
    $settingsWindow = Find-RiffloWindow $script:rifflo 'Rifflo 设置'
    $b = $settingsWindow.Bounds
    Save-ScreenCrop 'settings.png' $b.Left $b.Top ($b.Right - $b.Left) ($b.Bottom - $b.Top)
    Test-ScaledSettings $settings
    Test-IdlePreviewPlacement $settings
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-Diagnostics 'closing settings removes idle preview' { param($s) !$s.preview -and !$s.overlayLayout.captionScrolling } | Out-Null

    $player = Start-PlaybackFixture 'Target'
    Set-Playback $player 'Rifflo Position' 'low.wav' -Artist '4AGX'
    Wait-Diagnostics 'real overlay with live spectrum' { param($s) $s.title -eq 'Rifflo Position' -and $s.visible -and $s.capturing } | Out-Null
    Invoke-PlacementVerification -CrossMonitor:$CrossMonitor
}
try {
    $fixtureProject = Join-Path $repo 'tests\Rifflo.PlaybackFixture\Rifflo.PlaybackFixture.csproj'
    $script:fixture = Join-Path (Split-Path $fixtureProject) 'bin\Release\net10.0-windows10.0.26100.0\Rifflo.PlaybackFixture.exe'
    $localDotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
    if (!(Test-Path -LiteralPath $localDotnet)) { $localDotnet = (Get-Command dotnet).Source }
    & $localDotnet build $fixtureProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Building the Windows playback fixture failed.' }
    & $script:fixture --generate $script:tempDir
    if ($LASTEXITCODE -ne 0) { throw 'Generating the verification waveforms failed.' }
    if ($Mode -eq 'Broadcast') {
        & $script:fixture --generate-animation $script:tempDir
        if ($LASTEXITCODE -ne 0) { throw 'Generating the animated spectrum waveform failed.' }
    }
    if ($Mode -ne 'Fixtures') {
        if (!(Test-Path -LiteralPath $script:exe)) { throw "Release executable not found at $script:exe." }
        Initialize-DesktopInspection
    }
    $os = Get-CimInstance Win32_OperatingSystem
    Save-Evidence 'environment.json' ([pscustomobject]@{
        utc = [DateTimeOffset]::UtcNow.ToString('O'); os = $os.Caption; build = $os.BuildNumber
        osArchitecture = $os.OSArchitecture; logicalProcessorCount = [Environment]::ProcessorCount
        interactiveSessionId = (Get-Process -Id $PID).SessionId; powershellVersion = $PSVersionTable.PSVersion.ToString()
        isolatedSettingsPath = $script:settingsPath
        releaseSha256 = if ($Mode -ne 'Fixtures') { (Get-FileHash -LiteralPath $script:exe -Algorithm SHA256).Hash } else { $null }
        releaseDllSha256 = if ($Mode -ne 'Fixtures') { (Get-FileHash -LiteralPath (Join-Path (Split-Path $script:exe) 'Rifflo.dll') -Algorithm SHA256).Hash } else { $null }
        releaseVersion = if ($Mode -ne 'Fixtures') { (Get-Item -LiteralPath $script:exe).VersionInfo.ProductVersion } else { $null }
        releasePath = if ($Mode -ne 'Fixtures') { $script:exe } else { $null }
    })
    switch ($Mode) {
        'Fixtures' { Invoke-Fixtures }
        'Performance' { Invoke-Performance }
        'Edge' { Invoke-Edge }
        'Functional' { Invoke-Functional }
        'UI' { Invoke-DesktopVerification }
        'Overlay' { Invoke-OverlayVerification }
        'SessionChurn' { Invoke-SessionChurn }
        'Spectrum' { Invoke-SpectrumVerification }
        'Broadcast' { Invoke-BroadcastVerification }
    }
} catch {
    $exitCode = 1
    Save-Evidence 'error.json' ([pscustomobject]@{ message = $_.Exception.Message; scriptStackTrace = $_.ScriptStackTrace })
    Write-Warning $_.Exception.Message
    Write-Warning $_.ScriptStackTrace
} finally {
    for ($index = $script:ownedProcesses.Count - 1; $index -ge 0; $index--) {
        try { Stop-TestProcess $script:ownedProcesses[$index] }
        catch { Write-Warning $_.Exception.Message }
    }
    Save-Evidence 'assertions.json' $script:assertions.ToArray()
    if (@($script:assertions | Where-Object { !$_.passed }).Count -gt 0) { $exitCode = 1 }
    if ($KeepTemporaryFiles) { Write-Host "Temporary test resources: $($script:tempDir)" }
    else {
        $verifiedPath = [IO.Path]::GetFullPath($script:tempDir)
        $verifiedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
        if ((Split-Path $verifiedPath -Parent) -eq $verifiedTemp -and (Split-Path $verifiedPath -Leaf) -like 'RiffloVerification-*') {
            Remove-Item -LiteralPath $verifiedPath -Recurse -ErrorAction SilentlyContinue
        }
    }
}
exit $exitCode

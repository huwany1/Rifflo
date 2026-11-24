#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$PublishDirectory,
    [Parameter(Mandatory)] [string]$EvidenceDirectory,
    [ValidateSet('Baseline', 'KuGou', 'Fixture')] [string]$Mode = 'Fixture',
    [ValidateSet(30, 60)] [int]$FrameRate = 30,
    [switch]$SettingsClosed,
    [switch]$ExerciseControls,
    [switch]$OriginalPositionVisuallyAtStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Rifflo.Verification.Helpers.ps1')

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$script:exe = (Resolve-Path (Join-Path $PublishDirectory 'Rifflo.exe')).Path
$script:fixture = (Resolve-Path (Join-Path $repo 'tests\Rifflo.PlaybackFixture\bin\Release\net10.0-windows10.0.26100.0\Rifflo.PlaybackFixture.exe')).Path
$script:tempDir = Join-Path ([IO.Path]::GetTempPath()) ('RiffloVerification-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:tempDir -Force | Out-Null
$script:ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:assertions = [Collections.Generic.List[object]]::new()
$commands = [Collections.Generic.List[object]]::new()
$script:settingsPath = Join-Path $script:tempDir 'settings.json'
$script:diagPath = Join-Path $script:tempDir 'diagnostics.json'
$windowsPath = Join-Path $EvidenceDirectory 'windows.jsonl'
$tracePath = Join-Path $EvidenceDirectory 'release-events.jsonl'
$source = if ($Mode -eq 'Fixture') { 'Rifflo.Verification.Switch' } else { 'kugou' }
$environment = @{
    RIFFLO_SETTINGS_PATH = $script:settingsPath
    RIFFLO_DIAGNOSTICS = $script:diagPath
    RIFFLO_FRAME_TRACE = '1'
    RIFFLO_EVENT_TRACE = $tracePath
    RIFFLO_TEST_MEDIA_PREFIX = if ($Mode -eq 'Fixture') { 'Rifflo.Verification.' } else { $null }
}
$exitCode = 0
$original = $null
$release = $null
$watcher = $null

function Read-Lines([string]$Path) {
    if (![IO.File]::Exists($Path)) { return }
    $stream = [IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')
    try {
        $reader = [IO.StreamReader]::new($stream)
        try {
            while ($null -ne ($line = $reader.ReadLine())) {
                if ($line.EndsWith('}')) { $line | ConvertFrom-Json -AsHashtable }
            }
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Start-Release {
    Register-TestProcess (Start-Process -FilePath $script:exe -WorkingDirectory (Split-Path $script:exe) `
        -Environment $environment -PassThru)
}

function Mark([string]$Label, $Detail) {
    $stamp = [Diagnostics.Stopwatch]::GetTimestamp()
    $commands.Add([ordered]@{ label = $Label; stopwatchTimestamp = $stamp; detail = $Detail })
    $stamp
}

function Measure-Time([long]$Start, $Event) {
    if ($null -ne $Event) { [math]::Round(($Event.stopwatchTimestamp - $Start) * 1000 / [Diagnostics.Stopwatch]::Frequency, 2) }
}

function Read-Media([string]$Action) {
    $arguments = @('--media', $source)
    if ($Action) { $arguments += $Action.Split(' ') }
    $result = & $script:fixture @arguments | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "GSMTC $source $Action failed ($LASTEXITCODE)." }
    $result
}

function Wait-Release([string]$Title, [bool]$Visible = $true, [bool]$Capturing = $true) {
    Wait-Observation "$Title release state" { Read-JsonSnapshot $script:diagPath } {
        param($s) $s.title -ceq $Title -and $s.visible -eq $Visible -and $s.capturing -eq $Capturing
    } 13
}

function Check-Visibility([string]$Label, [long]$Start, [long]$End) {
    $events = @(Read-Lines $windowsPath | Where-Object { $_['stopwatchTimestamp'] -ge $Start -and $_['stopwatchTimestamp'] -le $End })
    $realHidden = @($events | Where-Object { $_.title -eq 'Rifflo 桌面悬浮窗' -and !$_.visible }).Count
    $previewShown = @($events | Where-Object { $_.title -eq 'Rifflo 外观预览' -and $_.visible }).Count
    Add-Assertion "$Label continuously shows the real window with zero sample previews" ($realHidden -eq 0 -and $previewShown -eq 0) @{ realHidden = $realHidden; previewShown = $previewShown }
}

function Check-Switch([string]$Label, [long]$Start, $Before, $After, [int]$ExpectedBandSide = 0) {
    $end = [Diagnostics.Stopwatch]::GetTimestamp()
    Check-Visibility $Label $Start $end
    $trace = @(Read-Lines $tracePath)
    $events = @($trace | Where-Object { $_.stopwatchTimestamp -ge $Start -and $_.stopwatchTimestamp -le $end })
    $oldOpen = $trace | Where-Object { $_.kind -eq 'AudioNativeOpened' -and $_.stopwatchTimestamp -lt $Start } | Select-Object -Last 1
    $newOpens = @($events | Where-Object kind -eq 'AudioNativeOpened')
    $resumed = $events | Where-Object kind -eq 'AudioResumeVerified' | Select-Object -Last 1
    $packet = $events | Where-Object kind -eq 'AudioFirstPacket' | Select-Object -Last 1
    $fft = $events | Where-Object kind -eq 'AudioFirstFft' | Select-Object -Last 1
    $bands = $events | Where-Object kind -eq 'AudioFirstBands' | Select-Object -Last 1
    $playing = $events | Where-Object { $_.kind -eq 'MediaObserved' -and $_['state'] -eq 'Playing' -and !$_['isTransition'] -and $_['title'] -ceq $After.title } | Select-Object -Last 1
    $playingAt = if ($playing) { $playing.observedAt }
    $detail = [ordered]@{
        beforeGeneration = $Before.captureGeneration; afterGeneration = $After.captureGeneration
        beforeOpenAttempts = $Before.captureAttemptCount; afterOpenAttempts = $After.captureAttemptCount
        nativeOpens = if ($Mode -eq 'Baseline') { $After.captureAttemptCount - $Before.captureAttemptCount } else { $newOpens.Count }
        openedBefore = $oldOpen; playing = $playing; resumed = $resumed
        firstPacket = $packet; firstFft = $fft; firstBands = $bands
        playingToFirstPacketMs = if ($playingAt) { Measure-Time $playingAt $packet }
        playingToFirstFftMs = if ($playingAt) { Measure-Time $playingAt $fft }
        playingToFirstBandsMs = if ($playingAt) { Measure-Time $playingAt $bands }
        fftToFirstBandsMs = if ($fft) { Measure-Time $fft.stopwatchTimestamp $bands }
    }
    $same = $Before.captureAttemptCount -eq $After.captureAttemptCount -and $newOpens.Count -eq 0
    if ($Mode -ne 'Baseline') {
        $same = $same -and $null -ne $oldOpen -and $null -ne $resumed -and $null -ne $fft -and $null -ne $bands -and $null -ne $packet -and
            $oldOpen.connectionId -eq $resumed.connectionId -and $resumed.connectionId -eq $packet.connectionId -and
            $packet.connectionId -eq $fft.connectionId -and $fft.connectionId -eq $bands.connectionId -and
            $oldOpen.processId -eq $resumed.processId -and $oldOpen.processCreatedAt -eq $resumed.processCreatedAt -and
            $oldOpen.nativeOpenCount -eq $bands.nativeOpenCount -and $oldOpen.sampleGeneration -ne $bands.sampleGeneration -and
            $packet.sampleGeneration -eq $fft.sampleGeneration -and $fft.sampleGeneration -eq $bands.sampleGeneration -and
            $oldOpen.endpointIds.Count -gt 0 -and ($oldOpen.endpointIds -join '|') -ceq ($resumed.endpointIds -join '|')
    }
    Add-Assertion "$Label keeps the original native recorder and delivers fresh FFT bands" $same $detail
    if ($Mode -ne 'Baseline') {
        Add-Assertion "$Label completes fresh FFT and commits the display within the normal timing window" `
            ($null -ne $detail.playingToFirstFftMs -and $null -ne $detail.playingToFirstBandsMs -and
                $detail.playingToFirstFftMs -ge 0 -and $detail.playingToFirstFftMs -lt 500 -and
                $detail.fftToFirstBandsMs -ge 0 -and $detail.fftToFirstBandsMs -lt 110) $detail
        $applied = @($events | Where-Object kind -eq 'MediaApplied')
        $transition = @($applied | Where-Object isTransition -eq $true)
        $wpfChanges = @($events | Where-Object { $_.kind -eq 'WindowVisibility' -and (($_['window'] -eq 'real' -and !$_['real']) -or ($_['window'] -eq 'preview' -and $_['preview'])) })
        $unexpected = @($applied | Where-Object { !$_['title'] -or $_['title'] -cnotin @($Before.title, $After.title) })
        $newApplied = $applied | Where-Object title -CEQ $After.title | Select-Object -First 1
        $rollback = @($applied | Where-Object { $newApplied -and $_.stopwatchTimestamp -gt $newApplied.stopwatchTimestamp -and $_['title'] -ceq $Before.title })
        Add-Assertion "$Label applies the new title without clearing or reverting, with no WPF visibility change" `
            ($applied.Count -gt 0 -and $wpfChanges.Count -eq 0 -and $unexpected.Count -eq 0 -and $rollback.Count -eq 0 -and $null -ne $newApplied) `
            @{ applied = $applied; transitionCount = $transition.Count; wpfChanges = $wpfChanges; unexpected = $unexpected; rollback = $rollback }
    }
    if ($ExpectedBandSide -ne 0 -and $Mode -ne 'Baseline') {
        $peaks = @($After.bands)
        $dominant = 0
        for ($index = 1; $index -lt $peaks.Count; $index++) {
            if ($peaks[$index] -gt $peaks[$dominant]) { $dominant = $index }
        }
        $firstMatches = $null -ne $fft -and $(if ($ExpectedBandSide -lt 0) { $fft.peakBand -lt 43 } else { $fft.peakBand -gt 55 })
        $settledMatches = if ($ExpectedBandSide -lt 0) { $dominant -lt $peaks.Count / 2 } else { $dominant -gt $peaks.Count / 2 }
        Add-Assertion "$Label first FFT and settled bars correspond to the new tone" ($firstMatches -and $settledMatches) `
            @{ firstFft = $fft; dominantRenderedBand = $dominant; renderedBandCount = $peaks.Count }
    }
}

function Set-Transition([object]$Player, [string]$Title, [string]$Tone) {
    Set-Playback $Player $Title $Tone 'stopped'
    Set-Playback $Player $Title $Tone 'changing'
    Set-Playback $Player $Title $Tone 'playing'
}

function Wait-TraceTransition([long]$Start) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $observation = Read-Lines $tracePath | Where-Object {
            $_.kind -eq 'MediaApplied' -and $_['isTransition'] -and $_.stopwatchTimestamp -ge $Start
        } | Select-Object -First 1
        if ($observation) { return $observation }
        Start-Sleep -Milliseconds 8
    } while ($timer.ElapsedMilliseconds -lt 2000)
    throw 'The fixture did not enter an observed track transition.'
}

function Start-Transition([object]$Player, [string]$Label, [string]$Title, [string]$Tone) {
    $start = Mark $Label $Title
    Set-Playback $Player $Title $Tone 'stopped' -NoWait
    Wait-TraceTransition $start
}

function Wait-InactiveRelease([string]$Label, [scriptblock]$Condition) {
    Wait-Observation $Label { Read-JsonSnapshot $script:diagPath } $Condition
}

function Get-OpenTrayItem([string]$Label) {
    $icon = Find-TrayRifflo
    $point = Get-UiaClickPoint $icon
    [RiffloVerificationNative]::Click($point.x, $point.y, $true)
    Wait-Observation "open Rifflo tray menu: $Label" {
        $windows = @([RiffloVerificationNative]::Windows($script:rifflo.Id) | Where-Object Visible)
        Find-UiaInWindows $windows $Label
    } { $true } 3
}

function Invoke-ExitBoundary([object]$Player) {
    Set-Playback $Player 'Before source exit' 'switch-low.wav'
    $baseline = Wait-Release 'Before source exit'
    $transition = Start-Transition $Player 'source-exit' 'Before source exit' 'switch-low.wav'
    $exitRequested = Mark 'source-exit-requested' $Player.process.Id
    Set-Playback $Player 'Before source exit' 'switch-low.wav' 'exit' -NoWait
    if (!$Player.process.WaitForExit(3500) -or $Player.process.ExitCode -ne 0) { throw 'Controlled player did not exit normally.' }
    $inactive = Wait-InactiveRelease 'clear the exited source' { param($s) !$s.visible -and !$s.capturing -and [string]::IsNullOrEmpty($s.title) }
    $exitEvents = @(Read-Lines $tracePath | Where-Object { $_.stopwatchTimestamp -ge $exitRequested })
    $oldClosed = $exitEvents | Where-Object { $_.kind -eq 'AudioNativeClosed' -and $_['processId'] -eq $Player.process.Id } | Select-Object -First 1
    $removed = $exitEvents | Where-Object { $_.kind -eq 'MediaApplied' -and [string]::IsNullOrEmpty($_['source']) } | Select-Object -First 1
    Add-Assertion 'fixture exits during transition and releases the recorder and title' `
        ($null -ne $oldClosed -and $null -ne $removed -and ($exitRequested - $transition.observedAt) * 1000 / [Diagnostics.Stopwatch]::Frequency -lt 300) `
        @{ previousCapture = $baseline.audioMatch; closed = $oldClosed; removed = $removed; last = $inactive }

    $restarted = Start-PlaybackFixture 'Switch'
    Set-Playback $restarted 'After source restart' 'switch-high.wav'
    $bound = Wait-Release 'After source restart'
    $opened = Read-Lines $tracePath | Where-Object { $_.kind -eq 'AudioNativeOpened' -and $_['processId'] -eq $restarted.process.Id } | Select-Object -Last 1
    Add-Assertion 'restarted fixture binds a different process instance and new recorder' `
        ($null -ne $oldClosed -and $null -ne $opened -and $oldClosed.processId -ne $opened.processId -and $oldClosed.processCreatedAt -ne $opened.processCreatedAt -and $oldClosed.connectionId -ne $opened.connectionId) `
        @{ closed = $oldClosed; opened = $opened; newCapture = $bound.audioMatch }
    $restarted
}

function Invoke-HandoffBoundary([object]$Player) {
    $other = Start-PlaybackFixture 'Handoff'
    Set-Playback $other 'Other active player' 'switch-low.wav'
    $before = Wait-Release 'After source restart'
    $start = Mark 'other-player-handoff' $other.process.Id
    Set-Playback $Player 'After source restart' 'switch-high.wav' 'stopped' -NoWait
    $selected = Wait-Release 'Other active player'
    Check-Visibility 'handoff to another player' $start ([Diagnostics.Stopwatch]::GetTimestamp())
    Add-Assertion 'other active player takes over with an independent recorder' `
        ($selected.source -ceq $other.aumid -and $selected.captureAttemptCount -gt $before.captureAttemptCount) `
        @{ oldCapture = $before.audioMatch; newCapture = $selected.audioMatch }
    Set-Playback $other 'Other active player' 'switch-low.wav' 'exit' -NoWait
    if (!$other.process.WaitForExit(3500)) { throw 'Handoff player did not exit.' }
}

function Invoke-ManualControls([object]$Player) {
    if ($SettingsClosed) { throw 'Manual controls require an opened settings window.' }
    Initialize-DesktopInspection
    $script:rifflo = $release
    $settingsWindow = Find-RiffloWindow $release 'Rifflo 设置'
    $settings = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$settingsWindow.Hwnd)
    Set-Playback $Player 'Manual control low' 'switch-low.wav'
    Wait-Release 'Manual control low' | Out-Null
    $hide = Get-OpenTrayItem '隐藏悬浮窗'
    $transition = Start-Transition $Player 'manual-hide' 'Manual control low' 'switch-low.wav'
    $hideAt = Mark 'manual-hide-click' $null
    Invoke-Uia $hide
    Set-Playback $Player 'Manual control high' 'switch-high.wav'
    $hidden = Wait-Release 'Manual control high' $false $false
    Add-Assertion 'manual hiding during transition survives new Playing' `
        (($hideAt - $transition.observedAt) * 1000 / [Diagnostics.Stopwatch]::Frequency -lt 300 -and !$hidden.visible -and !$hidden.capturing) `
        @{ transition = $transition; hidden = $hidden }
    Invoke-RiffloTray '恢复悬浮窗'
    Wait-Release 'Manual control high' | Out-Null

    foreach ($control in @('LiveCaptureEnabled', 'SpectrumEnabled')) {
        $toggle = (Get-SettingControl $settings $control).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        $current = Read-JsonSnapshot $script:diagPath
        $beforeTitle = $current.title
        $transition = Start-Transition $Player "disable-$control" $beforeTitle 'switch-high.wav'
        $changed = Mark "toggle-$control" $false
        $toggle.Toggle()
        Set-Playback $Player 'Manual control resumed' 'switch-high.wav'
        $off = Wait-Release 'Manual control resumed' $true $false
        $preserved = if ($control -eq 'SpectrumEnabled') { !$off.spectrumVisible } else { $off.spectrumMode -eq 'ManualPreset' }
        Add-Assertion "$control disabled during transition stays off after new Playing" `
            (($changed - $transition.observedAt) * 1000 / [Diagnostics.Stopwatch]::Frequency -lt 300 -and $preserved) `
            @{ transition = $transition; sample = $off }
        $toggle.Toggle()
        Wait-Release 'Manual control resumed' | Out-Null
    }

    $quit = Get-OpenTrayItem '退出'
    $transition = Start-Transition $Player 'rifflo-exit' 'Manual control resumed' 'switch-high.wav'
    $quitAt = Mark 'rifflo-exit-click' $null
    Invoke-Uia $quit
    if (!$release.WaitForExit(5500)) { throw 'Rifflo did not exit through its isolated tray menu.' }
    $shutdown = @(Read-Lines $tracePath | Where-Object { $_.stopwatchTimestamp -ge $quitAt })
    $stopped = $shutdown | Where-Object kind -EQ 'AppStopped' | Select-Object -First 1
    $closed = $shutdown | Where-Object kind -EQ 'AudioNativeClosed' | Select-Object -First 1
    Add-Assertion 'tray exit during transition stops workers and closes the connection normally' `
        (($quitAt - $transition.observedAt) * 1000 / [Diagnostics.Stopwatch]::Frequency -lt 300 -and $release.ExitCode -eq 0 -and
            $null -ne $stopped -and $stopped.workersStopped -and $null -ne $closed) `
        @{ transition = $transition; exitCode = $release.ExitCode; stopped = $stopped; closed = $closed }
}

try {
    if ($Mode -ne 'Fixture') {
        $original = Read-Media
        Save-Evidence 'kugou-before.json' $original
        if ($original.status -notin 'Playing', 'Paused') { throw "Cannot safely restore original KuGou status $($original.status)." }
    }
    @{ preferredSource = $source; animationFrameRate = $FrameRate } | ConvertTo-Json |
        Set-Content -LiteralPath $script:settingsPath -Encoding utf8
    $release = Start-Release
    $watcher = Start-Process -FilePath $script:fixture -ArgumentList @('--watch-visibility', $release.Id, "`"$windowsPath`"") `
        -WindowStyle Hidden -PassThru
    Wait-Observation 'native window event watcher' { @(Read-Lines $windowsPath) } { param($w) @($w).Count -gt 0 } 6 | Out-Null
    if (!$SettingsClosed) {
        $secondary = Start-Release
        if (!$secondary.WaitForExit(5000) -or $secondary.ExitCode -ne 0) { throw 'The second launch did not open the isolated settings instance.' }
        Wait-Observation 'settings native show event' { Read-Lines $windowsPath } {
            param($w) [bool]@($w | Where-Object { $_['title'] -eq 'Rifflo 设置' -and $_['visible'] }).Count
        } 8 | Out-Null
    }
    Save-Evidence 'environment.json' @{
        osVersion = [Environment]::OSVersion.VersionString; releasePath = $script:exe; releasePid = $release.Id
        releaseExeSha256 = (Get-FileHash -LiteralPath $script:exe).Hash
        releaseDllSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path $script:exe) 'Rifflo.dll')).Hash
        source = $source; mode = $Mode; frameRate = $FrameRate; settingsOpen = !$SettingsClosed
        stopwatchFrequency = [Diagnostics.Stopwatch]::Frequency
    }
    if ($Mode -eq 'Fixture') {
        & $script:fixture --generate-track-switch $script:tempDir
        if ($LASTEXITCODE -ne 0) { throw 'Fixture waveform generation failed.' }
        $player = Start-PlaybackFixture 'Switch'
        Set-Playback $player 'Switch 200 Hz' 'switch-low.wav'
        $before = Wait-Release 'Switch 200 Hz'
        foreach ($case in @(
            @{ name = 'low-to-high'; title = 'Switch 6000 Hz'; file = 'switch-high.wav'; side = 1 },
            @{ name = 'high-to-low'; title = 'Switch 200 Hz'; file = 'switch-low.wav'; side = -1 }
        )) {
            $start = Mark $case.name $case.title
            Set-Transition $player $case.title $case.file
            $first = Wait-Release $case.title
            $settled = Wait-Observation "$($case.name) settled bars" { Read-JsonSnapshot $script:diagPath } {
                param($s) $s.unixTimeMs -gt $first.unixTimeMs -and $s.visible -and $s.capturing
            }
            Check-Switch $case.name $start $before $settled $case.side
            $before = $settled
        }
        $start = Mark 'low-to-silent' 'zero-valued PCM'
        Set-Transition $player 'Switch Silence' 'switch-silence.wav'
        $first = Wait-InactiveRelease 'playing zero-valued PCM keeps an open recorder' {
            param($s) $s.title -ceq 'Switch Silence' -and $s.state -eq 'Playing' -and $s.visible -and $s.captureRunning
        }
        $silence = Wait-Observation 'silent signal bars settle to zero' { Read-JsonSnapshot $script:diagPath } {
            param($s) $s.unixTimeMs -gt $first.unixTimeMs -and $s.visible -and $s.captureRunning -and
                ($s.bands | Measure-Object -Maximum).Maximum -le 0.001 -and
                ($s.renderedBarHeights | Measure-Object -Maximum).Maximum -le 2.001
        } 8
        Check-Switch 'low-to-silent' $start $before $silence
        Add-Assertion 'zero-valued PCM keeps capture and quiet bars' ($silence.spectrumMode -in 'Muted', 'Live') $silence
        $stop = Mark 'repeated-stop' 'alternating Stopped and Changing across 300 ms'
        1..4 | ForEach-Object {
            Set-Playback $player 'Switch Silence' 'switch-silence.wav' 'stopped'
            Set-Playback $player 'Switch Silence' 'switch-silence.wav' 'changing'
        }
        $stopped = Wait-Observation 'stop expires under repeated interim states' { Read-JsonSnapshot $script:diagPath } {
            param($s) !$s.visible -and !$s.capturing -and [string]::IsNullOrEmpty($s.title)
        }
        $hidden = Read-Lines $windowsPath | Where-Object { $_['stopwatchTimestamp'] -ge $stop -and $_['title'] -eq 'Rifflo 桌面悬浮窗' -and !$_['visible'] } | Select-Object -First 1
        $firstInterim = Read-Lines $tracePath | Where-Object {
            $_.kind -eq 'MediaObserved' -and $_['isTransition'] -and $_.stopwatchTimestamp -ge $stop
        } | Select-Object -First 1
        $elapsed = if ($firstInterim) { Measure-Time $firstInterim.observedAt $hidden }
        Add-Assertion 'repeated stop cannot extend the window deadline' ($null -ne $elapsed -and $elapsed -ge 295 -and $elapsed -lt 415) `
            @{ millisecondsFromFirstInterim = $elapsed; firstInterim = $firstInterim; stopped = $stopped }
        $player = Invoke-ExitBoundary $player
        Invoke-HandoffBoundary $player
        if ($ExerciseControls) { Invoke-ManualControls $player }
    } else {
        $null = Mark 'play-original' $original.title
        if ($original.status -eq 'Paused') { $null = Read-Media 'play' }
        $before = Wait-Release $original.title
        $title = $original.title
        foreach ($direction in 'next', 'previous') {
            $start = Mark $direction $title
            $null = Read-Media $direction
            $after = Wait-Observation "KuGou $direction" { Read-JsonSnapshot $script:diagPath } {
                param($s) $s.source -eq 'kugou' -and $s.title -and $s.title -cne $title -and $s.visible -and $s.capturing
            }
            Check-Switch "KuGou $direction" $start $before $after
            $title = $after.title
            $before = $after
        }
        $start = Mark 'normal-pause' $title
        $null = Read-Media 'pause'
        $paused = Wait-Observation 'normal KuGou pause tail ends' { Read-JsonSnapshot $script:diagPath } {
            param($s) $s.state -eq 'Paused' -and !$s.visible -and !$s.capturing -and $s.preview -eq !$SettingsClosed
        }
        $hidden = Read-Lines $windowsPath | Where-Object {
            $_['stopwatchTimestamp'] -ge $start -and $_['title'] -eq 'Rifflo 桌面悬浮窗' -and !$_['visible']
        } | Select-Object -First 1
        $elapsed = if ($hidden) { ($hidden.stopwatchTimestamp - $start) * 1000 / [Diagnostics.Stopwatch]::Frequency }
        $pauseObserved = Read-Lines $tracePath | Where-Object {
            $_.kind -eq 'MediaObserved' -and $_['state'] -eq 'Paused' -and $_.stopwatchTimestamp -ge $start
        } | Select-Object -First 1
        Add-Assertion 'KuGou normal pause collapses at approximately two seconds' ($null -ne $elapsed -and $elapsed -ge 1800 -and $elapsed -lt 2300) `
            @{ millisecondsFromCommand = $elapsed; millisecondsFromPauseObservation = if ($pauseObserved) { Measure-Time $pauseObserved.observedAt $hidden }; hidden = $hidden; paused = $paused }
        $null = Mark 'normal-resume' $title
        $null = Read-Media 'play'
        $resumed = Wait-Release $title
        Add-Assertion 'KuGou resumes the real track and live capture after normal pause' ($resumed.title -ceq $title -and $resumed.visible) $resumed
        if ($Mode -ne 'Baseline') {
            $quickPause = Mark 'quick-pause' $title
            $null = Read-Media 'pause'
            Start-Sleep -Milliseconds 130
            $null = Mark 'quick-resume' $title
            $null = Read-Media 'play'
            Start-Sleep -Milliseconds 2400
            $quick = Wait-Release $title
            Check-Visibility 'KuGou quick pause and resume' $quickPause ([Diagnostics.Stopwatch]::GetTimestamp())
            Add-Assertion 'KuGou short pause resume remains live past the old pause deadline' ($quick.state -eq 'Playing' -and $quick.capturing) $quick
        }
    }
} catch {
    Save-Evidence 'error.json' @{ message = $_.Exception.Message; stack = $_.ScriptStackTrace }
    Write-Warning "$($_.Exception.Message) $($_.ScriptStackTrace)"
    $exitCode = 1
} finally {
    if ($original) {
        try {
            $current = Read-Media
            if ($current.title -cne $original.title) {
                $null = Read-Media 'previous'
                $current = Wait-Observation 'the original KuGou track' { Read-Media } { param($s) $s.title -ceq $original.title }
            }
            if ($original.isPlaybackPositionEnabled) { $null = Read-Media "seek $([long]$original.positionMs)" }
            if ($OriginalPositionVisuallyAtStart -and $original.status -eq 'Paused' -and !$original.isPlaybackPositionEnabled) {
                $null = Read-Media 'rewind-paused'
            } elseif ($original.status -ne $current.status) {
                $null = Read-Media $(if ($original.status -eq 'Paused') { 'pause' } else { 'play' })
            }
            $restored = Wait-Observation 'the original KuGou track and playback status' { Read-Media } {
                param($s) $s.title -ceq $original.title -and $s.status -eq $original.status
            }
            Save-Evidence 'kugou-restored.json' $restored
        } catch {
            Save-Evidence 'restore-error.json' @{ message = $_.Exception.Message; original = $original }
            Write-Warning "KuGou restoration needs inspection: $($_.Exception.Message)"
            $exitCode = 1
        }
    }
    foreach ($process in $script:ownedProcesses) { Stop-TestProcess $process }
    if ($watcher -and !$watcher.WaitForExit(3000)) { Stop-TestProcess $watcher }
    Save-Evidence 'commands.json' $commands.ToArray()
    Save-Evidence 'assertions.json' $script:assertions.ToArray()
    if (@($script:assertions | Where-Object { !$_.passed }).Count -gt 0) { $exitCode = 1 }
    $safeTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $verified = (Resolve-Path -LiteralPath $script:tempDir).Path
    if ((Split-Path $verified -Parent) -eq $safeTempRoot -and (Split-Path $verified -Leaf) -like 'RiffloVerification-*') {
        Remove-Item -LiteralPath $verified -Recurse
    }
}
exit $exitCode

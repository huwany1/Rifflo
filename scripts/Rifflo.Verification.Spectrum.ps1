function Read-SpectrumSamples([object]$First, [int]$Count) {
    $sample = $First
    $sample
    for ($index = 1; $index -lt $Count; $index++) {
        $previousTime = $sample.unixTimeMs
        $sample = Wait-Diagnostics 'next distinct spectrum sample' { param($s) $s.unixTimeMs -gt $previousTime } 8
        $sample
    }
}

function Test-NoCaptureActivity([object[]]$Samples) {
    $first = $Samples[0]
    @($Samples | Where-Object {
        $_.captureRunning -or $_.packetCount -ne $first.packetCount -or $_.fftCount -ne $first.fftCount -or
        $_.matchAttemptCount -ne $first.matchAttemptCount -or $_.captureAttemptCount -ne $first.captureAttemptCount
    }).Count -eq 0
}

function Get-SettingToggleState([object]$Root, [string]$Id) {
    (Get-SettingControl $Root $Id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString()
}

function Read-SpectrumSettingStatus([object]$Settings, [string]$Text) {
    Wait-Observation "settings status containing $Text" {
        $element = Get-SettingControl $Settings 'PlaybackStatus'
        [pscustomobject]@{ text = $element.Current.Name; offscreen = $element.Current.IsOffscreen }
    } { param($s) !$s.offscreen -and $s.text.Contains($Text) } 5
}

function Close-SpectrumSettings([object]$Settings) {
    $Settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
}

function Save-SpectrumSettingsImage([string]$FileName) {
    $window = Find-RiffloWindow $script:rifflo 'Rifflo 设置'
    $bounds = $window.Bounds
    Save-ScreenCrop $FileName $bounds.Left $bounds.Top ($bounds.Right - $bounds.Left) ($bounds.Bottom - $bounds.Top)
}

function Show-SpectrumControlRegion([object]$Settings) {
    $scroll = (Get-SettingControl $Settings 'SettingsOptions').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 73) }
}

function Test-RenderedPreset([object[]]$Samples, [string]$Mode, [int]$BarCount) {
    $unexpected = @($Samples | Where-Object {
        $_.spectrumMode -ne $Mode -or !$_.spectrumVisible -or !$_.visible -or
        $_.renderedBarHeights.Count -ne $BarCount
    })
    $heightSignatures = @($Samples | ForEach-Object { ($_.renderedBarHeights | ForEach-Object { [math]::Round($_, 1) }) -join ',' } | Sort-Object -Unique)
    $unexpected.Count -eq 0 -and $heightSignatures.Count -eq $Samples.Count -and $Samples[-1].drawCount -gt $Samples[0].drawCount
}

function Invoke-SpectrumVerification {
    $script:rifflo = Start-Rifflo
    $idle = Wait-Diagnostics 'this release starts idle with both toggles enabled' {
        param($s) $s.processId -eq $script:rifflo.Id -and !$s.visible -and $s.spectrumEnabled -and $s.liveCaptureEnabled
    }
    Add-Assertion 'published process starts with display and live capture enabled' ($script:rifflo.Path -eq $script:exe) $idle
    Invoke-RiffloTray '设置'
    $settings = Get-ProductWindowRoot
    $show = Get-SettingControl $settings 'SpectrumEnabled'
    $live = Get-SettingControl $settings 'LiveCaptureEnabled'
    $defaultShow = Get-SettingToggleState $settings 'SpectrumEnabled'
    $defaultLive = Get-SettingToggleState $settings 'LiveCaptureEnabled'
    Add-Assertion 'the real settings UI has two distinct enabled switches' `
        ($show.Current.Name -eq '显示频谱' -and $live.Current.Name -eq '采集实时频谱' -and $defaultShow -eq 'On' -and $defaultLive -eq 'On') `
        @{ display = $show.Current.Name; capture = $live.Current.Name; displayDefault = $defaultShow; captureDefault = $defaultLive }
    $preview = Wait-Diagnostics 'animated settings preview without playing audio' { param($s) $s.preview -and $s.spectrumMode -eq 'Preview' -and $s.previewDrawCount -gt 0 }
    $previewSamples = @(Read-SpectrumSamples $preview 3)
    $previewStatus = Read-SpectrumSettingStatus $settings '预览'
    Close-SpectrumSettings $settings
    $closed = Wait-Diagnostics 'closing settings stops idle preview' { param($s) !$s.preview -and !$s.visible -and !$s.frameTimerRunning }
    $closedSamples = @(Read-SpectrumSamples $closed 2)
    Save-Evidence '01-preview.json' @{ preview = $previewSamples; afterClose = $closedSamples; ui = $previewStatus }
    Add-Assertion 'idle preview draws without capture or a failure hint and stops on close' (
        (Test-NoCaptureActivity ($previewSamples + $closedSamples)) -and $previewSamples[-1].previewDrawCount -gt $preview.previewDrawCount -and
        $closedSamples[0].previewDrawCount -eq $closedSamples[-1].previewDrawCount -and !$previewStatus.text.Contains('无法')
    ) @{ previewDrawCounts = $previewSamples.previewDrawCount; closedDrawCounts = $closedSamples.previewDrawCount }

    $player = Start-PlaybackFixture 'SpectrumPrimary'
    $lowTitle = 'Rifflo spectrum 200 Hz'
    $highTitle = 'Rifflo spectrum 6000 Hz'
    Set-Playback $player $lowTitle 'low.wav'
    $low = Wait-Diagnostics '200 Hz live peak' {
        param($s) $peak = Get-BandEvidence $s; $s.title -eq $lowTitle -and $s.spectrumMode -eq 'Live' -and $peak.normalizedIndex -lt 0.45 -and $peak.peak -gt 0.05
    }
    Set-Playback $player $highTitle 'high.wav'
    $high = Wait-Diagnostics '6000 Hz live peak' {
        param($s) $peak = Get-BandEvidence $s; $s.title -eq $highTitle -and $s.spectrumMode -eq 'Live' -and $peak.normalizedIndex -gt 0.65 -and $peak.peak -gt 0.05
    }
    Invoke-RiffloTray '设置'
    $settings = Get-ProductWindowRoot
    $liveStatus = Read-SpectrumSettingStatus $settings '正在显示实时频谱'
    $lowRender = Get-BandEvidence @{ bands = $low.renderedBarHeights }
    $highRender = Get-BandEvidence @{ bands = $high.renderedBarHeights }
    Save-Evidence '02-live-frequencies.json' @{ low = $low; high = $high; lowPeak = Get-BandEvidence $low; highPeak = Get-BandEvidence $high; lowRender = $lowRender; highRender = $highRender; ui = $liveStatus }
    Add-Assertion '200 Hz and 6000 Hz tones from the same source reach the matching rendered band and reuse capture' (
        $low.captureRunning -and $high.captureRunning -and $high.captureAttemptCount -eq $low.captureAttemptCount -and
        $high.packetCount -gt $low.packetCount -and $high.fftCount -gt $low.fftCount -and $high.renderedBarHeights.Count -eq 16 -and
        $lowRender.normalizedIndex -lt 0.45 -and $highRender.normalizedIndex -gt 0.65
    ) @{ dominantBands = @((Get-BandEvidence $low).dominant, (Get-BandEvidence $high).dominant); packetCounts = @($low.packetCount, $high.packetCount); captureAttempts = @($low.captureAttemptCount, $high.captureAttemptCount) }

    Set-SettingToggle $settings 'SpectrumEnabled' $false
    $displayOff = Wait-Diagnostics 'UI display-off releases an active live recorder' {
        param($s) $s.visible -and $s.spectrumMode -eq 'Disabled' -and !$s.captureRunning -and $s.liveCaptureEnabled
    }
    $displayOffSamples = @(Read-SpectrumSamples $displayOff 3)
    Set-SettingToggle $settings 'SpectrumEnabled' $true
    $displayOn = Wait-Diagnostics 'display-on restores the saved live capture mode' { param($s) $s.spectrumMode -eq 'Live' -and $s.captureRunning }
    Save-Evidence '02-live-display-toggle.json' @{ displayOff = $displayOffSamples; displayOn = $displayOn }
    Add-Assertion 'display-off releases live recording while preserving its saved preference and display-on restores it' (
        (Test-NoCaptureActivity $displayOffSamples) -and $displayOff.renderedBarHeights.Count -eq 0 -and $displayOn.liveCaptureEnabled
    ) @{ before = $high.captureState; off = $displayOff.captureState; on = $displayOn.captureState }

    Set-SettingToggle $settings 'LiveCaptureEnabled' $false
    $manual = Wait-Diagnostics 'UI toggle starts manual preset and releases capture' { param($s) $s.spectrumMode -eq 'ManualPreset' -and !$s.captureRunning }
    $manualStatus = Read-SpectrumSettingStatus $settings '实时采集已关闭'
    $manualSamples = @(Read-SpectrumSamples $manual 4)
    $saved = Wait-Observation 'manual capture preference written' { Read-JsonSnapshot $script:settingsPath } { param($s) !$s.liveCaptureEnabled -and $s.spectrumEnabled } 5
    Save-Evidence '03-manual-vertical.json' @{ samples = $manualSamples; ui = $manualStatus; settings = $saved }
    Show-SpectrumControlRegion $settings
    Save-SpectrumSettingsImage '03-manual-vertical-settings.png'
    Add-Assertion 'manual preset animates on 16 vertical-layout bars and releases capture, FFT and retry work' (
        (Test-RenderedPreset $manualSamples 'ManualPreset' 16) -and (Test-NoCaptureActivity $manualSamples) -and
        (Test-LayoutAlignment $manual.overlayLayout 0) -and $manual.width -eq $high.width -and $manual.height -eq $high.height -and
        $manual.x -eq $high.x -and $manual.y -eq $high.y
    ) @{ first = $manual; last = $manualSamples[-1] }

    $scroll = (Get-SettingControl $settings 'SettingsOptions').GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 0) }
    Select-SettingOption $settings 'Layout' '左歌名、右频谱'
    Show-SpectrumControlRegion $settings
    Set-SettingNumber $settings 'BarCount' 24
    $horizontal = Wait-Diagnostics '24-bar horizontal preset' { param($s) $s.spectrumMode -eq 'ManualPreset' -and $s.renderedBarHeights.Count -eq 24 -and (Test-LayoutAlignment $s.overlayLayout 2) }
    $horizontalSamples = @(Read-SpectrumSamples $horizontal 4)
    Save-Evidence '04-manual-horizontal.json' $horizontalSamples
    Add-Assertion '24 bars animate in the horizontal layout' (Test-RenderedPreset $horizontalSamples 'ManualPreset' 24) @{ draws = $horizontalSamples.drawCount; barCount = $horizontal.renderedBarHeights.Count }

    $longTitle = ('Rifflo verification - ' + ('A long title that scrolls while the spectrum is hidden - ' * 3)).TrimEnd()
    Set-Playback $player $longTitle 'low.wav'
    Wait-Diagnostics 'manual preset persists on a new track' { param($s) $s.title -eq $longTitle -and $s.spectrumMode -eq 'ManualPreset' -and !$s.captureRunning } | Out-Null
    Set-SettingToggle $settings 'SpectrumEnabled' $false
    $onlyCaption = Wait-Diagnostics 'disabled spectrum with scrolling long caption' {
        param($s) $s.title -eq $longTitle -and $s.spectrumMode -eq 'Disabled' -and !$s.spectrumVisible -and $s.visible -and $s.overlayLayout.captionOffset -gt 2
    }
    $captionSamples = @(Read-SpectrumSamples $onlyCaption 3)
    $disabledStatus = Read-SpectrumSettingStatus $settings '频谱显示已关闭'
    $retainedSetting = Get-SettingToggleState $settings 'LiveCaptureEnabled'
    Save-Evidence '05-caption-only.json' @{ samples = $captionSamples; ui = $disabledStatus; retainedSetting = $retainedSetting }
    Add-Assertion 'hiding the spectrum removes its geometry and draw activity while the caption keeps scrolling' (
        (Test-NoCaptureActivity $captionSamples) -and $retainedSetting -eq 'Off' -and $null -eq $onlyCaption.overlayLayout.spectrum -and
        $captionSamples[-1].drawCount -eq $onlyCaption.drawCount -and $captionSamples[-1].overlayLayout.captionOffset -ne $onlyCaption.overlayLayout.captionOffset
    ) @{ offsets = $captionSamples.overlayLayout.captionOffset; draws = $captionSamples.drawCount }
    Set-SettingToggle $settings 'SpectrumEnabled' $true
    Wait-Diagnostics 're-enabled display respects disabled capture' { param($s) $s.spectrumMode -eq 'ManualPreset' -and !$s.captureRunning } | Out-Null
    Set-SettingToggle $settings 'SpectrumEnabled' $false
    $beforeRestart = Wait-Observation 'both disabled switches are persisted after operating settings UI' {
        Read-JsonSnapshot $script:settingsPath
    } { param($s) !$s.spectrumEnabled -and !$s.liveCaptureEnabled } 5
    Invoke-RiffloTray '退出'
    if (!$script:rifflo.WaitForExit(7000)) { throw 'The release instance did not exit from its tray menu.' }
    $oldProcessId = $script:rifflo.Id
    $script:rifflo = Start-Rifflo
    $restart = Wait-Diagnostics 'new PID starts with both settings off for the still-playing track' {
        param($s) $s.processId -eq $script:rifflo.Id -and $s.title -eq $longTitle -and $s.spectrumMode -eq 'Disabled' -and !$s.liveCaptureEnabled
    }
    Invoke-RiffloTray '设置'
    $settings = Get-ProductWindowRoot
    $restartShow = Get-SettingToggleState $settings 'SpectrumEnabled'
    $restartLive = Get-SettingToggleState $settings 'LiveCaptureEnabled'
    Set-SettingToggle $settings 'SpectrumEnabled' $true
    $restoredManual = Wait-Diagnostics 'display restores saved manual preset after restart' {
        param($s) $s.spectrumMode -eq 'ManualPreset' -and !$s.captureRunning -and $s.renderedBarHeights.Count -eq 24
    }
    Save-Evidence '05-switches-after-restart.json' @{ before = $beforeRestart; pidBefore = $oldProcessId; restart = $restart; restoredManual = $restoredManual }
    Add-Assertion 'both options survive an EXE restart and enabling display restores manual preset for the still-playing track' (
        $oldProcessId -ne $restart.processId -and !$restart.spectrumEnabled -and $restart.captureAttemptCount -eq 0 -and
        $restartShow -eq 'Off' -and $restartLive -eq 'Off' -and $restoredManual.captureAttemptCount -eq 0
    ) @{ pidBefore = $oldProcessId; pidAfter = $restart.processId; restartedDisplay = $restartShow; restartedCapture = $restartLive; restoredMode = $restoredManual.spectrumMode }

    $lossTitle = 'Rifflo spectrum same source reconnect'
    Set-Playback $player $lossTitle 'low.wav' -AudioDisconnected
    Wait-Diagnostics 'playing metadata remains while the source is disconnected' { param($s) $s.title -eq $lossTitle -and $s.state -eq 'Playing' } | Out-Null
    $retryClock = [Diagnostics.Stopwatch]::StartNew()
    Set-SettingToggle $settings 'LiveCaptureEnabled' $true
    $connecting = Wait-Diagnostics 'initial connection grace is visible' { param($s) $s.spectrumMode -eq 'Connecting' } 6
    $connectingStatus = Read-SpectrumSettingStatus $settings '正在连接播放器音频'
    $fallback = Wait-Diagnostics 'audio unavailable falls back to preset' { param($s) $s.spectrumMode -eq 'AutomaticPreset' } 12
    $fallbackMs = $retryClock.ElapsedMilliseconds
    $fallbackStatus = Read-SpectrumSettingStatus $settings '预制动画'
    $fallbackSamples = @(Read-SpectrumSamples $fallback 5)
    $fallbackSettings = Read-JsonSnapshot $script:settingsPath
    Set-Playback $player $lossTitle 'low.wav'
    $recovered = Wait-Diagnostics 'same process, source and title recover automatically' {
        param($s) $s.spectrumMode -eq 'Live' -and $s.captureRunning -and (Get-BandEvidence $s).peak -gt 0.05
    } 22
    $recoveredStatus = Read-SpectrumSettingStatus $settings '正在显示实时频谱'
    Save-Evidence '06-fallback-and-recovery.json' @{
        fixturePid = $player.process.Id; connecting = $connecting; connectingUi = $connectingStatus; fallback = $fallbackSamples
        fallbackMs = $fallbackMs; fallbackUi = $fallbackStatus; settings = $fallbackSettings; recovered = $recovered; recoveredUi = $recoveredStatus
    }
    Add-Assertion 'bounded connection grace, automatic preset and retry preserve capture preference and recover the same player' (
        $fallbackMs -le 11000 -and $fallback.spectrumReason -ne 'None' -and $fallbackStatus.text.Contains('无法') -and
        $fallback.source -eq $recovered.source -and $fallback.title -eq $recovered.title -and $fallbackSettings.liveCaptureEnabled -and
        $recovered.audioMatch.targetProcessId -eq $player.process.Id -and
        (Test-RenderedPreset $fallbackSamples 'AutomaticPreset' 24) -and $fallbackSamples[-1].matchAttemptCount -gt $fallback.matchAttemptCount -and
        $connecting.width -eq $fallback.width -and $fallback.width -eq $recovered.width -and $connecting.height -eq $fallback.height -and $fallback.height -eq $recovered.height
    ) @{ elapsedMs = $fallbackMs; reason = $fallback.spectrumReason; attempts = $fallbackSamples.matchAttemptCount; recoverySource = $recovered.source }

    Set-Playback $player $lossTitle 'low.wav' -AudioDisconnected
    Wait-Diagnostics 'same player disconnects again and enters automatic fallback' { param($s) $s.spectrumMode -eq 'AutomaticPreset' } 18 | Out-Null
    Set-SettingToggle $settings 'LiveCaptureEnabled' $false
    $cancel = Wait-Diagnostics 'switching off during automatic fallback cancels retries' { param($s) $s.spectrumMode -eq 'ManualPreset' -and !$s.captureRunning }
    Set-Playback $player $lossTitle 'low.wav'
    $cancelledSamples = @(Read-SpectrumSamples $cancel 6)
    Set-SettingToggle $settings 'LiveCaptureEnabled' $true
    $reenabled = Wait-Diagnostics 'only explicit reenable restarts audio after manual cancellation' { param($s) $s.spectrumMode -eq 'Live' -and $s.captureRunning }
    Save-Evidence '06-manual-retry-cancellation.json' @{ cancelled = $cancelledSamples; reenabled = $reenabled }
    Add-Assertion 'manual off cancels pending fallback retries even when the original audio returns' (
        (Test-NoCaptureActivity $cancelledSamples) -and (Test-RenderedPreset $cancelledSamples 'ManualPreset' 24) -and
        $reenabled.captureAttemptCount -gt $cancelledSamples[-1].captureAttemptCount
    ) @{ matchAttempts = $cancelledSamples.matchAttemptCount; captureAttempts = $cancelledSamples.captureAttemptCount; reenabled = $reenabled.captureAttemptCount }

    $silenceTitle = 'Rifflo spectrum high, 12 seconds silent PCM, high'
    Set-Playback $player $silenceTitle 'long-silence-high.wav'
    $lead = Wait-Diagnostics 'continuous fixture starts with an audible high tone' {
        param($s) $s.title -eq $silenceTitle -and $s.spectrumMode -eq 'Live' -and (Get-BandEvidence $s).highPeak -gt 0.08
    }
    $silence = Wait-Diagnostics 'continuous stream goes quiet' { param($s) (Get-BandEvidence $s).peak -lt 0.04 } 10
    $silenceSamples = @(Read-SpectrumSamples $silence 8)
    $tail = Wait-Diagnostics 'same PCM stream sounds again after silence' {
        param($s) $s.title -eq $silenceTitle -and $s.spectrumMode -eq 'Live' -and (Get-BandEvidence $s).highPeak -gt 0.08
    } 13
    Save-Evidence '07-silent-pcm.json' @{ lead = $lead; silence = $silenceSamples; resumed = $tail }
    Add-Assertion 'healthy same-source silence stays live for more than the no-packet timeout with quiet bars and advancing packets' (
        $silenceSamples[-1].unixTimeMs - $silence.unixTimeMs -gt 6000 -and
        @($silenceSamples | Where-Object { $_.spectrumMode -ne 'Live' -or !$_.captureRunning -or (Get-BandEvidence $_).peak -ge 0.04 }).Count -eq 0 -and
        $silenceSamples[-1].packetCount -gt $silence.packetCount -and $tail.captureAttemptCount -eq $lead.captureAttemptCount
    ) @{ elapsedMs = $silenceSamples[-1].unixTimeMs - $silence.unixTimeMs; packets = $silenceSamples.packetCount; heights = $silenceSamples[-1].renderedBarHeights; captureAttempts = @($lead.captureAttemptCount, $tail.captureAttemptCount) }

    try {
        $muteProbe = (& $script:fixture --mute-fixture-session $player.process.Id true | ConvertFrom-Json)
        if ($LASTEXITCODE -ne 0 -or $muteProbe.changed -lt 1) { throw 'Could not verify native mixer mute on the active fixture audio session.' }
        $muted = Wait-Diagnostics 'native mixer mute remains quiet' {
            param($s) $s.spectrumMode -eq 'Muted' -and (Get-BandEvidence $s).peak -lt 0.04
        }
        $mutedStatus = Read-SpectrumSettingStatus $settings '播放器已静音'
    } finally {
        $unmutedProbe = (& $script:fixture --mute-fixture-session $player.process.Id false | ConvertFrom-Json)
    }
    $unmuted = Wait-Diagnostics 'native mixer unmute resumes live capture' { param($s) $s.spectrumMode -eq 'Live' -and $s.captureRunning } 16
    $unmutedStatus = Read-SpectrumSettingStatus $settings '正在显示实时频谱'
    Save-Evidence '08-mixer-mute.json' @{ mute = $muteProbe; muted = $muted; mutedUi = $mutedStatus; unmute = $unmutedProbe; unmuted = $unmuted; unmutedUi = $unmutedStatus }
    Add-Assertion 'OS mixer mute and unmute switch between muted and live for the same source' ($unmutedProbe.changed -gt 0 -and $muted.source -eq $unmuted.source) @{ mute = $muteProbe; unmute = $unmutedProbe }

    Invoke-SpectrumVisibilityVerification $settings $player $silenceTitle
}

function Invoke-SpectrumVisibilityVerification([object]$Settings, [object]$Player, [string]$Title) {
    Set-SettingToggle $Settings 'LiveCaptureEnabled' $false
    $preset = Wait-Diagnostics 'manual preset before pause' { param($s) $s.spectrumMode -eq 'ManualPreset' -and $s.visible }
    $pauseClock = [Diagnostics.Stopwatch]::StartNew()
    Set-Playback $Player $Title 'long-silence-high.wav' 'paused'
    $pausing = Wait-Diagnostics 'pause enters real overlay tail' { param($s) $s.visible -and $s.spectrumMode -eq 'Pausing' } 4
    $pausedStatus = Read-SpectrumSettingStatus $Settings '暂停'
    Close-SpectrumSettings $Settings
    while ((Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗') -and $pauseClock.ElapsedMilliseconds -lt 4500) {
        Start-Sleep -Milliseconds 35
    }
    $hideMs = $pauseClock.ElapsedMilliseconds
    $paused = Wait-Diagnostics 'paused real track and finished drawing after tail' { param($s) $s.state -eq 'Paused' -and !$s.visible -and !$s.frameTimerRunning }
    $pausedSamples = @(Read-SpectrumSamples $paused 2)
    Save-Evidence '09-pause.json' @{ before = $preset; tail = $pausing; ui = $pausedStatus; after = $pausedSamples; hideMs = $hideMs }
    Add-Assertion 'pause renders a tail and hides after about two seconds without continuing to draw' (
        $pausing.renderedBarHeights.Count -eq $preset.renderedBarHeights.Count -and $hideMs -ge 1550 -and $hideMs -le 3100 -and
        (Test-NoCaptureActivity $pausedSamples) -and $pausedSamples[0].drawCount -eq $pausedSamples[-1].drawCount
    ) @{ hideMs = $hideMs; tailDraws = $pausing.drawCount; stoppedDraws = $pausedSamples.drawCount }

    Set-Playback $Player 'Rifflo rapidly switched 200 Hz' 'low.wav'
    Wait-Diagnostics 'manual preset resumes' { param($s) $s.spectrumMode -eq 'ManualPreset' -and $s.visible } | Out-Null
    Invoke-RiffloTray '设置'
    $Settings = Get-ProductWindowRoot
    $other = Start-PlaybackFixture 'SpectrumAlternate'
    Set-SettingToggle $Settings 'LiveCaptureEnabled' $true
    Set-SettingToggle $Settings 'SpectrumEnabled' $false
    Set-Playback $other 'Rifflo rapidly switched 6000 Hz' 'high.wav' -NoWait
    Set-Playback $Player 'Rifflo rapidly switched 200 Hz' 'low.wav' 'paused'
    Set-SettingToggle $Settings 'SpectrumEnabled' $true
    Set-SettingToggle $Settings 'LiveCaptureEnabled' $false
    $latest = Wait-Diagnostics 'final preset has alternate source after rapid show, live and player changes' {
        param($s) $s.source -eq $other.aumid -and $s.title -eq 'Rifflo rapidly switched 6000 Hz' -and $s.spectrumMode -eq 'ManualPreset' -and !$s.captureRunning
    }
    $raceSamples = @(Read-SpectrumSamples $latest 5)
    $raceStatus = Read-SpectrumSettingStatus $Settings '实时采集已关闭'
    Set-SettingToggle $Settings 'LiveCaptureEnabled' $true
    $alternateLive = Wait-Diagnostics 'reenabling live capture uses the alternate player high tone' {
        param($s) $s.source -eq $other.aumid -and $s.spectrumMode -eq 'Live' -and $s.captureRunning -and (Get-BandEvidence $s).highPeak -gt 0.08
    }
    Save-Evidence '10-rapid-switches.json' @{ samples = $raceSamples; ui = $raceStatus; alternateLive = $alternateLive }
    Add-Assertion 'late capture work cannot overwrite the final toggles or selected player' (
        (Test-NoCaptureActivity $raceSamples) -and (Test-RenderedPreset $raceSamples 'ManualPreset' 24) -and
        @($raceSamples | Where-Object { $_.source -ne $other.aumid }).Count -eq 0
    ) @{ final = $latest.spectrumMode; source = $alternateLive.source; alternatePeak = Get-BandEvidence $alternateLive }

    Invoke-RiffloTray '隐藏悬浮窗'
    $hidden = Wait-Diagnostics 'tray-hide stops real drawing, frame timer and capture' {
        param($s) $s.manuallyHidden -and !$s.visible -and !$s.captureRunning -and !$s.frameTimerRunning
    }
    Set-Playback $other 'New alternate song while hidden' 'high.wav'
    $hiddenSong = Wait-Diagnostics 'new song remains hidden' { param($s) $s.title -eq 'New alternate song while hidden' -and $s.spectrumMode -eq 'Hidden' }
    $hiddenSamples = @($hidden) + @(Read-SpectrumSamples $hiddenSong 3)
    $hiddenStatus = Read-SpectrumSettingStatus $Settings '悬浮窗已隐藏'
    Invoke-RiffloTray '恢复悬浮窗'
    $restored = Wait-Diagnostics 'restore resumes live capture' { param($s) !$s.manuallyHidden -and $s.visible -and $s.spectrumMode -eq 'Live' -and $s.captureRunning }
    Save-Evidence '11-tray-hide-and-restore.json' @{ hidden = $hiddenSamples; ui = $hiddenStatus; restored = $restored }
    Add-Assertion 'tray hide persists across track changes, stops real drawing and capture, and restores live mode' (
        (Test-NoCaptureActivity $hiddenSamples) -and $hiddenSamples[0].drawCount -eq $hiddenSamples[-1].drawCount -and
        $hiddenSamples[0].renderedBarHeights.Count -eq 0 -and $restored.title -eq $hiddenSong.title
    ) @{ draws = $hiddenSamples.drawCount; restoredSource = $restored.source }

    Close-SpectrumSettings $Settings
    Set-Playback $other 'No active track after stop' 'high.wav' 'stopped'
    $stopped = Wait-Diagnostics 'stop clears real playback display and capture' { param($s) !$s.visible -and !$s.preview -and !$s.frameTimerRunning -and !$s.captureRunning }
    $stoppedSamples = @(Read-SpectrumSamples $stopped 2)
    Add-Assertion 'stopping playback leaves no real window, frame activity or capture retries' (
        (Test-NoCaptureActivity $stoppedSamples) -and $stoppedSamples[0].drawCount -eq $stoppedSamples[-1].drawCount -and
        $null -eq (Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗')
    ) $stoppedSamples
    Invoke-RiffloTray '退出'
    if (!$script:rifflo.WaitForExit(7000)) { throw 'The verification instance did not close before testing a legacy profile.' }

    @{ spectrumEnabled = $false; textWidth = 280; preferredSource = $other.aumid } |
        ConvertTo-Json | Set-Content -LiteralPath $script:settingsPath -Encoding utf8
    Set-Playback $other 'Rifflo legacy profile with display off' 'high.wav'
    $script:rifflo = Start-Rifflo
    $legacy = Wait-Diagnostics 'old profile retains disabled display and defaults to enabled capture preference' {
        param($s) $s.processId -eq $script:rifflo.Id -and $s.visible -and $s.spectrumMode -eq 'Disabled' -and $s.liveCaptureEnabled
    }
    Invoke-RiffloTray '设置'
    $Settings = Get-ProductWindowRoot
    $legacyShow = Get-SettingToggleState $Settings 'SpectrumEnabled'
    $legacyLive = Get-SettingToggleState $Settings 'LiveCaptureEnabled'
    Set-SettingToggle $Settings 'LiveCaptureEnabled' $false
    Invoke-Uia (Get-SettingControl $Settings 'ResetDefaults')
    $defaults = Wait-Diagnostics 'restore defaults turns on both spectrum toggles' {
        param($s) $s.spectrumEnabled -and $s.liveCaptureEnabled -and $s.spectrumMode -eq 'Live'
    }
    $defaultSettings = Wait-Observation 'reset is written to isolated settings' { Read-JsonSnapshot $script:settingsPath } { param($s) $s.spectrumEnabled -and $s.liveCaptureEnabled } 5
    Save-Evidence '12-legacy-and-defaults.json' @{ legacy = $legacy; restored = $defaults; settings = $defaultSettings }
    Add-Assertion 'legacy display-off profile remains caption only and restore-defaults enables both switches' (
        $legacyShow -eq 'Off' -and $legacyLive -eq 'On' -and !$legacy.captureRunning -and !$legacy.spectrumVisible -and $defaults.captureRunning
    ) @{ oldDisplay = $legacyShow; oldCapture = $legacyLive; restoredDisplay = $defaultSettings.spectrumEnabled; restoredCapture = $defaultSettings.liveCaptureEnabled }
    Close-SpectrumSettings $Settings
}

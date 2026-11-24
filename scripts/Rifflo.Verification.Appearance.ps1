function Test-LayoutAlignment([object]$Layout, [int]$Selection) {
    $caption = $Layout.caption
    $spectrum = $Layout.spectrum
    if ($null -eq $caption -or $null -eq $spectrum -or $spectrum.width -le 0 -or $spectrum.height -le 0) { return $false }
    $leftAligned = [math]::Abs($caption.x - $spectrum.x) -le 0.1
    $bottomAligned = [math]::Abs(($caption.y + $caption.height) - ($spectrum.y + $spectrum.height)) -le 0.1
    switch ($Selection) {
        0 { return $leftAligned -and ($spectrum.y + $spectrum.height) -lt $caption.y }
        1 { return $leftAligned -and ($caption.y + $caption.height) -lt $spectrum.y }
        2 { return $bottomAligned -and ($caption.x + $caption.width) -lt $spectrum.x }
        3 { return $bottomAligned -and ($spectrum.x + $spectrum.width) -lt $caption.x }
    }
}

function Save-OverlayImage([string]$Name) {
    $window = Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗'
    if ($null -eq $window) { throw 'The playing overlay disappeared before taking its screenshot.' }
    $b = $window.Bounds
    Save-ScreenCrop $Name $b.Left $b.Top ($b.Right - $b.Left) ($b.Bottom - $b.Top)
}

function Get-ReadyAppearanceSettings {
    Wait-Observation 'appearance settings expose the scroll-mode control' {
        $window = Find-RiffloWindow $script:rifflo 'Rifflo 设置'
        if ($null -eq $window) { return }
        try {
            $settings = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$window.Hwnd)
            if ($settings.Current.ProcessId -eq $script:rifflo.Id -and (Find-UiaExact $settings 'ScrollMode' 'AutomationId')) {
                $settings
            }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
    } { $true } 5
}

function Open-AppearanceSettings {
    $activation = Start-Rifflo
    if (!$activation.WaitForExit(5000)) { throw 'The settings activation should exit after waking the existing instance.' }
    Get-ReadyAppearanceSettings
}

function Measure-CaptionScroll([object]$Player, [string]$Title, [string]$Artist, [string]$Mode) {
    Set-Playback $Player 'Short' 'low.wav' -Artist ''
    Wait-Diagnostics 'short song before a new long song' { param($s) $s.title -eq 'Short' -and !$s.overlayLayout.captionScrolling } | Out-Null
    Set-Playback $Player $Title 'low.wav' -Artist $Artist
    $first = Wait-Diagnostics "$Mode starts at the new song" { param($s) $s.title -eq $Title -and $s.artist -eq $Artist -and $s.overlayLayout.captionScrolling }
    $limit = $first.overlayLayout.captionContentWidth - $first.overlayLayout.caption.width
    Save-OverlayImage "$Mode-start.png"

    $samples = [System.Collections.Generic.List[object]]::new()
    $samples.Add($first)
    $terminalIndex = -1
    $repeatAt = -1
    $afterEnd = $null
    $time = [Diagnostics.Stopwatch]::StartNew()
    $previous = $first
    while ($time.Elapsed.TotalSeconds -lt (8 + $limit / 30)) {
        $sample = Wait-Diagnostics "$Mode next sample" { param($s) $s.unixTimeMs -gt $previous.unixTimeMs -and $s.title -eq $Title } 4
        $samples.Add($sample)
        $offset = $sample.overlayLayout.captionOffset
        if ($repeatAt -eq -1 -and $offset -gt 3 -and $offset -lt $limit - 12) {
            $repeatAt = $samples.Count - 1
            Set-Playback $Player $Title 'low.wav' -Artist $Artist
        }
        if ($terminalIndex -eq -1 -and [math]::Abs($offset - $limit) -le 0.1) {
            $terminalIndex = $samples.Count - 1
            Save-OverlayImage "$Mode-end.png"
        } elseif ($terminalIndex -ge 0 -and $offset -lt $limit - 2) {
            $afterEnd = $sample
            break
        }
        $previous = $sample
    }
    $observed = $samples.ToArray()
    Save-Evidence "$Mode-samples.json" $observed
    $outbound = @($observed | Select-Object -First ([math]::Max(0, $terminalIndex)) | Where-Object { $_.overlayLayout.captionOffset -gt 1 })
    $atEnd = @($observed | Where-Object { [math]::Abs($_.overlayLayout.captionOffset - $limit) -le 0.1 })
    $endHold = if ($atEnd.Count -gt 1) { $atEnd[-1].unixTimeMs - $atEnd[0].unixTimeMs } else { 0 }
    $tracedEndHold = $null
    $heldAtEnd = $endHold -ge 850
    if ($env:RIFFLO_FRAME_TRACE -eq '1' -and $first.PSObject.Properties['captionFrames']) {
        $commits = @($observed | ForEach-Object captionFrames | Where-Object {
            $_.timestamp -ge $first.stopwatchTimestamp
        } | Sort-Object timestamp -Unique)
        $atEndCommit = $commits | Where-Object { [math]::Abs($_.value - $limit) -le 0.0001 } | Select-Object -First 1
        $afterEndCommit = if ($null -ne $atEndCommit) {
            $commits | Where-Object { $_.timestamp -gt $atEndCommit.timestamp -and $_.value -lt $limit } | Select-Object -First 1
        }
        if ($null -ne $afterEndCommit) {
            $tracedEndHold = 1000 * ($afterEndCommit.timestamp - $atEndCommit.timestamp) / $first.stopwatchFrequency
        }
        $heldAtEnd = $null -ne $tracedEndHold -and [math]::Abs($tracedEndHold - 2000) -le 120
    }
    $zero = @($observed | Select-Object -First ([math]::Max(0, $terminalIndex)) | Where-Object { $_.overlayLayout.captionOffset -lt 0.1 })
    $firstMove = if ($outbound.Count) { $outbound[0].unixTimeMs - $first.unixTimeMs } else { 0 }
    $moved = $outbound.Count -ge 2
    if ($moved) {
        $rate = ($outbound[-1].overlayLayout.captionOffset - $outbound[0].overlayLayout.captionOffset) / (($outbound[-1].unixTimeMs - $outbound[0].unixTimeMs) / 1000)
        $moved = $rate -gt 26 -and $rate -lt 34
    } else { $rate = 0 }
    $continued = $repeatAt -ge 0 -and $observed.Count -gt ($repeatAt + 1) -and
        $observed[$repeatAt + 1].overlayLayout.captionOffset -gt $observed[$repeatAt].overlayLayout.captionOffset
    $geometry = $first.overlayLayout | Select-Object caption,spectrum
    $geometryJson = $geometry | ConvertTo-Json -Compress
    $fixed = @($observed | Where-Object {
        $_.x -ne $first.x -or $_.y -ne $first.y -or $_.width -ne $first.width -or $_.height -ne $first.height -or
        ($_.overlayLayout | Select-Object caption,spectrum | ConvertTo-Json -Compress) -ne $geometryJson
    }).Count -eq 0
    $afterOffset = if ($null -ne $afterEnd) { $afterEnd.overlayLayout.captionOffset } else { -1 }
    $endAction = if ($Mode -eq 'loop') { [math]::Abs($afterOffset) -le 0.1 } else { $afterOffset -gt 1 -and $afterOffset -lt $limit - 1 }
    Add-Assertion "$Mode starts from zero, advances near 30 DIP/s, dwells at the full extent and takes its selected return path" `
        ($zero.Count -ge 1 -and $firstMove -ge 850 -and $firstMove -lt 3400 -and $moved -and $heldAtEnd -and $endAction) `
        @{ limitDip = $limit; outboundDipPerSecond = $rate; firstMovementMs = $firstMove; sampledEndHoldMs = $endHold; tracedEndHoldMs = $tracedEndHold; firstOffsetAfterEnd = $afterOffset }
    Add-Assertion "$Mode keeps the window, viewport and spectrum fixed through a duplicate update" ($fixed -and $continued) `
        @{ repeatedAtSample = $repeatAt; geometry = $geometry }
}

function Test-SameCaptionNewTrack([object]$Player, [string]$Title, [string]$Artist) {
    Set-Playback $Player 'Short' 'low.wav' -Artist ''
    Wait-Diagnostics 'short song before same-caption track check' { param($s) $s.title -eq 'Short' } | Out-Null
    Set-Playback $Player $Title 'low.wav' -Artist $Artist -AlbumTitle 'Rifflo test album' -TrackNumber 1
    $outbound = Wait-Diagnostics 'first numbered track reaches the outbound segment' {
        param($s) $s.title -eq $Title -and $s.artist -eq $Artist -and $s.overlayLayout.captionOffset -gt 15 -and $s.overlayLayout.captionOffset -lt 75
    }
    Set-Playback $Player $Title 'low.wav' -Artist $Artist -AlbumTitle 'Rifflo test album' -TrackNumber 2
    $restarted = Wait-Diagnostics 'new numbered track with the same caption resets to zero' {
        param($s) $s.unixTimeMs -gt $outbound.unixTimeMs -and $s.captionRevision -gt $outbound.captionRevision -and
            $s.title -eq $Title -and $s.artist -eq $Artist -and
            $s.overlayLayout.captionScrolling -and $s.overlayLayout.captionOffset -eq 0
    } 4
    Add-Assertion 'same title and artist restart only when the selected track actually changes' `
        ($restarted.overlayLayout.captionContentWidth -eq $outbound.overlayLayout.captionContentWidth) `
        @{ firstTrackNumber = 1; secondTrackNumber = 2; beforeRevision = $outbound.captionRevision; afterRevision = $restarted.captionRevision;
            before = $outbound.overlayLayout; after = $restarted.overlayLayout }
}

function Invoke-OverlayVerification {
    $player = Start-PlaybackFixture 'Overlay'
    Set-Playback $player 'Short' 'low.wav' -Artist ''
    $initialOptions = @{ preferredSource = $player.aumid } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($script:settingsPath, $initialOptions)
    $script:rifflo = Start-Rifflo
    $initial = Wait-Diagnostics 'short caption and actual spectrum from the fixture' {
        param($s) $s.source -eq $player.aumid -and $s.title -eq 'Short' -and $s.visible -and $null -ne $s.overlayLayout.spectrum
    }
    Add-Assertion 'short caption is stationary at the default width' `
        ($initial.overlayLayout.caption.width -eq 260 -and !$initial.overlayLayout.captionScrolling -and $initial.overlayLayout.captionOffset -eq 0) $initial.overlayLayout

    # Separate the test overlay from the running desktop user's overlay before sampling pixels.
    $window = Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗'
    $caption = $initial.overlayLayout.caption
    $scale = $initial.overlayLayout.dpiScale
    [RiffloVerificationNative]::Drag(($window.Bounds.Left + [int](($caption.x + 8) * $scale)),
        ($window.Bounds.Top + [int](($caption.y + $caption.height / 2) * $scale)), 240, -165)
    $positioned = Wait-Diagnostics 'test overlay separated from the desktop overlay' { param($s) $s.x -ne $initial.x -and $s.y -ne $initial.y }
    $window = Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗'
    $background = Start-ClickSurface $window 'dark' (Join-Path $script:tempDir 'appearance-clicks.txt')
    Wait-Observation 'controlled appearance background' { Find-RiffloWindow $background 'Rifflo verification click surface' } { $true } | Out-Null

    $settings = Open-AppearanceSettings
    Set-SettingNumber $settings 'TextWidth' 160
    $title = 'That Girl&Talking to the Moon (Full)'
    $artist = '4AGX'
    Set-Playback $player $title 'low.wav' -Artist $artist
    $layoutNames = @('上频谱、下歌名','上歌名、下频谱','左歌名、右频谱','左频谱、右歌名')
    $modeNames = @('单向循环','左右往返')
    $matrix = @(for ($layout = 0; $layout -lt $layoutNames.Count; $layout++) {
        Select-SettingOption $settings 'Layout' $layoutNames[$layout]
        for ($mode = 0; $mode -lt $modeNames.Count; $mode++) {
            Select-SettingOption $settings 'ScrollMode' $modeNames[$mode]
            $stored = Wait-Observation 'layout and mode saved by settings UI' { Read-JsonSnapshot $script:settingsPath } {
                param($config) $config.layout -eq $layout -and $config.scrollMode -eq $mode -and $config.textWidth -eq 160
            } 7
            $observed = Wait-Diagnostics 'layout and scrolling applied to live playback' {
                param($s) $s.title -eq $title -and $s.artist -eq $artist -and $s.overlayLayout.captionScrolling -and
                    [math]::Abs($s.overlayLayout.caption.width - 160) -le 0.5 -and (Test-LayoutAlignment $s.overlayLayout $layout)
            }
            if ($mode -eq 0) { Save-OverlayImage "layout-$layout.png" }
            [pscustomobject]@{ layout = $layoutNames[$layout]; mode = $modeNames[$mode]; config = $stored; snapshot = $observed }
        }
    })
    Save-Evidence 'layout-mode-matrix.json' $matrix
    Add-Assertion 'all layout and scroll selections apply immediately, align and preserve a valid saved position' `
        ($matrix.Count -eq 8 -and @($matrix | Where-Object { $_.snapshot.x -ne $positioned.x -or $_.snapshot.y -ne $positioned.y }).Count -eq 0) `
        @{ combinations = $matrix.Count; x = $positioned.x; y = $positioned.y }

    Select-SettingOption $settings 'Layout' $layoutNames[0]
    Select-SettingOption $settings 'ScrollMode' $modeNames[0]
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Measure-CaptionScroll $player $title $artist 'loop'
    $settings = Open-AppearanceSettings
    Select-SettingOption $settings 'ScrollMode' $modeNames[1]
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Measure-CaptionScroll $player $title $artist 'bounce'
    Test-SameCaptionNewTrack $player $title $artist

    $settings = Open-AppearanceSettings
    Set-SettingNumber $settings 'TextWidth' 640
    $wide = Wait-Diagnostics 'widening a moving caption stops and resets it' {
        param($s) $s.overlayLayout.caption.width -ge 639.5 -and !$s.overlayLayout.captionScrolling -and $s.overlayLayout.captionOffset -eq 0
    }
    $smallestFitting = [math]::Ceiling($wide.overlayLayout.captionContentWidth)
    Set-SettingNumber $settings 'TextWidth' $smallestFitting
    $fit = Wait-Diagnostics 'caption at the first integer width where the complete line fits' {
        param($s) [math]::Abs($s.overlayLayout.caption.width - $smallestFitting) -le 0.5 -and !$s.overlayLayout.captionScrolling
    }
    Set-SettingNumber $settings 'TextWidth' ($smallestFitting - 2)
    $overflow = Wait-Diagnostics 'slightly narrower viewport begins scrolling' {
        param($s) [math]::Abs($s.overlayLayout.caption.width - ($smallestFitting - 2)) -le 0.5 -and $s.overlayLayout.captionScrolling
    }
    Add-Assertion 'viewport changes recompute fine-grained overflow and reset static captions' `
        ($wide.overlayLayout.captionOffset -eq 0 -and $fit.overlayLayout.captionOffset -eq 0 -and $overflow.overlayLayout.captionScrolling) `
        @{ wide = $wide.overlayLayout; fit = $fit.overlayLayout; overflow = $overflow.overlayLayout }

    $mixedTitle = '中英混排：Testing 🎶／全角・VeryLongWordWithoutAnySpace'
    $mixedArtist = '艺人 Artists 🎸 · More and more uninterrupted characters'
    Set-SettingNumber $settings 'TextWidth' 160
    Set-Playback $player $mixedTitle 'low.wav' -Artist $mixedArtist
    $mixed = Wait-Diagnostics 'mixed title and artist scroll as one line' {
        param($s) $s.title -eq $mixedTitle -and $s.artist -eq $mixedArtist -and $s.overlayLayout.captionOffset -gt 10
    }
    Set-SettingToggle $settings 'SpectrumEnabled' $false
    $textOnly = Wait-Diagnostics 'text-only mixed caption moves without audio capture' {
        param($s) !$s.capturing -and $null -eq $s.overlayLayout.spectrum -and $s.overlayLayout.captionOffset -gt 15
    }
    Set-SettingToggle $settings 'ShowArtist' $false
    $hiddenArtist = Wait-Diagnostics 'hiding the artist removes its measured text and separator' {
        param($s) $s.overlayLayout.captionContentWidth -lt $textOnly.overlayLayout.captionContentWidth
    }
    Set-Playback $player $mixedTitle 'low.wav' -Artist ''
    Wait-Diagnostics 'fixture announces the empty artist' { param($s) $s.artist -eq '' } | Out-Null
    Set-SettingToggle $settings 'ShowArtist' $true
    $emptyArtist = Wait-Diagnostics 'empty artist leaves a long title scrolling alone' {
        param($s) $s.overlayLayout.captionScrolling -and $s.artist -eq '' -and
            $s.overlayLayout.captionContentWidth -eq $hiddenArtist.overlayLayout.captionContentWidth
    }
    Set-Playback $player 'Short' 'low.wav' -Artist ''
    $shortAgain = Wait-Diagnostics 'long title cut to a stationary short song' {
        param($s) $s.title -eq 'Short' -and !$s.overlayLayout.captionScrolling -and $s.overlayLayout.captionOffset -eq 0
    }
    Add-Assertion 'mixed text, artist hiding and empty artist, and long-to-short playback use the same fixed viewport' `
        ($mixed.overlayLayout.caption.width -eq $shortAgain.overlayLayout.caption.width -and $null -eq $textOnly.overlayLayout.spectrum -and
            $textOnly.overlayLayout.captionContentWidth -gt $emptyArtist.overlayLayout.captionContentWidth) `
        @{ mixed = $mixed.overlayLayout; textOnly = $textOnly.overlayLayout; emptyArtist = $emptyArtist.overlayLayout; short = $shortAgain.overlayLayout }
    Set-SettingToggle $settings 'SpectrumEnabled' $true
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Set-Playback $player $mixedTitle 'low.wav' -Artist $mixedArtist
    Wait-Diagnostics 'short-to-long playback restores genuine spectrum and scrolling' {
        param($s) $s.title -eq $mixedTitle -and $null -ne $s.overlayLayout.spectrum -and $s.capturing -and $s.overlayLayout.captionScrolling
    } | Out-Null
    Invoke-RiffloTray '隐藏悬浮窗'
    $hidden = Wait-Diagnostics 'manual hide stops caption and capture' {
        param($s) $s.manuallyHidden -and !$s.visible -and !$s.capturing -and !$s.overlayLayout.captionScrolling
    }
    Set-Playback $player $title 'low.wav' -Artist $artist
    $hiddenNext = Wait-Diagnostics 'fresh metadata does not restart a hidden caption' { param($s) $s.title -eq $title }
    Invoke-RiffloTray '恢复悬浮窗'
    $resumed = Wait-Diagnostics 'restoring the overlay restarts the caption' {
        param($s) $s.visible -and $s.overlayLayout.captionScrolling
    }
    Add-Assertion 'manual hiding stops refresh across new tracks and restoration begins at the left' `
        (!$hiddenNext.visible -and !$hiddenNext.capturing -and !$hiddenNext.overlayLayout.captionScrolling -and
            $resumed.overlayLayout.captionOffset -eq 0 -and $resumed.x -eq $positioned.x -and $resumed.y -eq $positioned.y) `
        @{ hidden = $hidden.overlayLayout; afterTrackChange = $hiddenNext.overlayLayout; restored = $resumed.overlayLayout }
    Stop-TestProcess $background

    Set-Playback $player $title 'low.wav' 'stopped' -Artist $artist
    Wait-Diagnostics 'stopped fixture leaves no playing display' { param($s) !$s.visible -and !$s.capturing } | Out-Null
    $settings = Open-AppearanceSettings
    Select-SettingOption $settings 'Layout' $layoutNames[3]
    $preview = Wait-Diagnostics 'appearance preview shows long sample without audio capture' {
        param($s) $s.preview -and !$s.capturing -and (Test-LayoutAlignment $s.overlayLayout 3) -and $s.overlayLayout.captionOffset -gt 3
    }
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $closed = Wait-Diagnostics 'closing settings stops preview refresh' { param($s) !$s.preview -and !$s.overlayLayout.captionScrolling }
    Set-Playback $player $title 'low.wav' -Artist $artist
    $real = Wait-Diagnostics 'resumed playback uses the same preview settings' {
        param($s) $s.title -eq $title -and $s.visible -and !$s.preview -and (Test-LayoutAlignment $s.overlayLayout 3)
    }
    Add-Assertion 'idle preview scrolls independently and real playback restores its layout and position' `
        ($preview.x -eq $real.x -and $preview.y -eq $real.y -and $preview.overlayLayout.caption.width -eq $real.overlayLayout.caption.width) `
        @{ preview = $preview; closed = $closed; real = $real }

    $settings = Open-AppearanceSettings
    Set-SettingToggle $settings 'PositionLocked' $true
    Wait-Observation 'non-default layout, width, mode and lock saved before restart' { Read-JsonSnapshot $script:settingsPath } {
        param($s) $s.layout -eq 3 -and $s.textWidth -eq 160 -and $s.scrollMode -eq 1 -and $s.positionLocked
    } 5 | Out-Null
    $settingsHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $script:settingsPath).Hash
    $previousPid = $script:rifflo.Id
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Invoke-RiffloTray '退出'
    if (!$script:rifflo.WaitForExit(7000)) { throw 'Overlay did not exit normally for the settings restart test.' }
    Remove-Item -LiteralPath $script:diagPath -ErrorAction SilentlyContinue
    $script:rifflo = Start-Rifflo
    $afterRestart = Wait-Diagnostics 'restarted overlay adopts the saved layout, width, and click-through' {
        param($s) $s.visible -and $s.title -eq $title -and $s.positionLocked -and (Test-LayoutAlignment $s.overlayLayout 3) -and
            [math]::Abs($s.overlayLayout.caption.width - 160) -le 0.5
    }
    Invoke-RiffloTray '设置'
    $settings = Get-ReadyAppearanceSettings
    $scrollMode = Get-SettingControl $settings 'ScrollMode'
    $selected = $scrollMode.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    $restartedSettingsHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $script:settingsPath).Hash
    Add-Assertion 'layout, width, scroll mode and click-through survive normal exit and restart' `
        ($selected.Count -eq 1 -and $selected[0].Current.Name -eq $modeNames[1] -and $restartedSettingsHash -eq $settingsHash -and
            $script:rifflo.Id -ne $previousPid -and $afterRestart.processId -eq $script:rifflo.Id -and $script:rifflo.Path -eq $script:exe) `
        @{ snapshot = $afterRestart; selected = @($selected | ForEach-Object { $_.Current.Name }); previousPid = $previousPid;
            restartedPid = $script:rifflo.Id; path = $script:rifflo.Path; savedSha256 = $settingsHash; reloadedSha256 = $restartedSettingsHash }

    Invoke-Uia (Get-SettingControl $settings 'ResetDefaults')
    $defaults = Wait-Observation 'restore defaults resets all new fields' { Read-JsonSnapshot $script:settingsPath } {
        param($s) $s.layout -eq 0 -and $s.textWidth -eq 260 -and $s.scrollMode -eq 0 -and !$s.positionLocked -and $null -eq $s.position
    } 5
    $defaultWindow = Wait-Diagnostics 'overlay immediately applies restored defaults' {
        param($s) $s.visible -and !$s.positionLocked -and $s.overlayLayout.caption.width -eq 260 -and (Test-LayoutAlignment $s.overlayLayout 0)
    }
    Add-Assertion 'restore defaults resets every new setting and updates the desktop overlay' $true @{ config = $defaults; snapshot = $defaultWindow }
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Invoke-RiffloTray '退出'
    Add-Assertion 'published overlay exits normally through its own isolated tray icon' ($script:rifflo.WaitForExit(7000)) $script:rifflo.Id
}

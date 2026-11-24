$script:broadcastWindowTitle = 'Rifflo 桌面悬浮窗'
$script:broadcastItemName = '直播捕获模式'

function Get-BroadcastSetting([object]$Settings) {
    $control = Get-SettingControl $Settings 'BroadcastCaptureEnabled'
    $toggle = $control.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
}

function Get-BroadcastTrayState([switch]$Click) {
    $icon = Find-TrayRifflo
    $point = Get-UiaClickPoint $icon
    [RiffloVerificationNative]::Click($point.x, $point.y, $true)
    $clicked = $false
    try {
        $item = Wait-Observation 'broadcast tray menu item' {
            Find-UiaInWindows @([RiffloVerificationNative]::Windows($script:rifflo.Id) | Where-Object Visible) $script:broadcastItemName
        } { $true } 3
        $point = Get-UiaClickPoint $item
        $hit = [RiffloVerificationNative]::WindowAt($point.x, $point.y)
        if ($hit.ProcessId -ne $script:rifflo.Id) { throw 'The test instance tray menu is obscured.' }
        $toggle = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) {
            $checked = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
        } else {
            $checked = [RiffloVerificationNative]::MenuItemChecked($point.x, $point.y)
        }
        if ($Click) {
            [RiffloVerificationNative]::Click($point.x, $point.y, $false)
            $clicked = $true
        }
        return $checked
    } finally {
        if (!$clicked) {
            [RiffloVerificationNative]::SetKey(0x1B, $true)
            [RiffloVerificationNative]::SetKey(0x1B, $false)
        }
    }
}

function Wait-BroadcastWindow([bool]$Enabled, [bool]$Locked, [bool]$Topmost = $true) {
    $expected = 0x08080000 -bor $(if (!$Enabled) { 0x80 } else { 0 }) -bor
        $(if ($Locked) { 0x20 } else { 0 }) -bor $(if ($Topmost) { 0x08 } else { 0 })
    Wait-Observation 'real overlay native styles' {
        Find-RiffloWindow $script:rifflo $script:broadcastWindowTitle
    } { param($w) ($w.ExtendedStyle -band 0x080800A8) -eq $expected } 5
}

function Test-BroadcastBounds([object]$Before, [object]$After) {
    $Before.Hwnd -eq $After.Hwnd -and $Before.Bounds.Left -eq $After.Bounds.Left -and
        $Before.Bounds.Top -eq $After.Bounds.Top -and $Before.Bounds.Right -eq $After.Bounds.Right -and
        $Before.Bounds.Bottom -eq $After.Bounds.Bottom
}

function Assert-BroadcastProcessPath {
    $script:rifflo.Refresh()
    $actualPath = $script:rifflo.MainModule.FileName
    Add-Assertion 'test process runs the requested published executable' ($actualPath -eq $script:exe) @{
        pid = $script:rifflo.Id; actualPath = $actualPath; expectedPath = $script:exe
    }
}

function Test-BroadcastClickThrough([object]$Overlay) {
    $clickLog = Join-Path $script:tempDir 'broadcast-clicks.txt'
    foreach ($shade in @('light', 'dark')) {
        $surface = Start-ClickSurface $Overlay $shade $clickLog
        try {
            $backdrop = Wait-Observation "$shade background" {
                Find-RiffloWindow $surface 'Rifflo verification click surface'
            } { $true }
            [RiffloVerificationNative]::Click(($backdrop.Bounds.Left + 4), ($backdrop.Bounds.Top + 4), $false)
            foreach ($part in @('title', 'spectrum')) {
                $point = Get-OverlayDragPoint $Overlay (Get-Diagnostics) $part
                $before = [RiffloVerificationNative]::Foreground()
                $focusBefore = [RiffloVerificationNative]::KeyboardFocus()
                $hit = [RiffloVerificationNative]::WindowAt($point.x, $point.y)
                $clicks = Get-ClickCount $clickLog
                [RiffloVerificationNative]::Click($point.x, $point.y, $false)
                $received = Wait-Observation "$shade backdrop receives the click on $part" {
                    Get-ClickCount $clickLog
                } { param($count) $count -gt $clicks } 4
                $after = [RiffloVerificationNative]::Foreground()
                $focusAfter = [RiffloVerificationNative]::KeyboardFocus()
                Add-Assertion "broadcast lock passes $part click to $shade backdrop without changing focus" (
                    $hit.ProcessId -eq $surface.Id -and $before.ProcessId -eq $surface.Id -and
                    $after.Hwnd -eq $before.Hwnd -and $focusBefore -ne 0 -and $focusAfter -eq $focusBefore -and
                    $received -eq $clicks + 1
                ) @{ pointer = $point; hit = $hit; foregroundBefore = $before; foregroundAfter = $after; focusBefore = $focusBefore; focusAfter = $focusAfter }
            }
            $b = $Overlay.Bounds
            $image = "broadcast-lock-$shade.png"
            Save-ScreenCrop $image ($b.Left - 8) ($b.Top - 8) ($b.Right - $b.Left + 16) ($b.Bottom - $b.Top + 16)
            $pixels = Read-OverlayPixels $image ($b.Left - 8)
            Add-Assertion "broadcast desktop spectrum and visible backdrop pixels on $shade" (
                $pixels.spectrumPixels -gt 20 -and $pixels.matchingBackgroundFraction -gt 0.4
            ) $pixels
        } finally { Stop-TestProcess $surface }
    }
}

function Test-BroadcastAppearance([object]$Settings, [long]$Handle, [object]$Player) {
    $layouts = @('上频谱、下歌名','上歌名、下频谱','左歌名、右频谱','左频谱、右歌名')
    $positions = for ($layout = 0; $layout -lt $layouts.Count; $layout++) {
        Select-SettingOption $Settings 'Layout' $layouts[$layout]
        $sample = Wait-Diagnostics 'broadcast layout aligns on the same window' {
            param($s) $s.visible -and $s.overlayWindowHandle -eq $Handle -and (Test-LayoutAlignment $s.overlayLayout $layout)
        }
        [pscustomobject]@{ layout = $layouts[$layout]; geometry = $sample.overlayLayout; hwnd = $sample.overlayWindowHandle }
    }
    Add-Assertion 'four layouts render correctly without changing the broadcast HWND' ($positions.Count -eq 4) $positions

    Select-SettingOption $Settings 'Layout' $layouts[0]
    Set-SettingNumber $Settings 'TextWidth' 160
    $newTitle = 'A second long title rolls to the end during capture verification'
    Set-Playback $Player $newTitle 'dynamic.wav' -Artist 'Rifflo artist'
    foreach ($mode in @('Live', 'ManualPreset')) {
        $live = $mode -eq 'Live'
        Set-SettingToggle $Settings 'LiveCaptureEnabled' $live
        $cadence = foreach ($rate in @(30, 60)) {
            Set-SettingToggle $Settings 'Animation60Fps' ($rate -eq 60)
            $from = Wait-Diagnostics "long title scrolling at $rate FPS with $mode spectrum" {
                param($s) $s.title -eq $newTitle -and $s.visible -and $s.overlayWindowHandle -eq $Handle -and
                    $s.animationFrameRate -eq $rate -and $s.spectrumMode -eq $mode -and
                    $s.liveCaptureEnabled -eq $live -and $s.captureRunning -eq $live -and $s.overlayLayout.captionOffset -gt 5
            }
            $to = Wait-Diagnostics 'subsequent frame rate sample' {
                param($s) $s.stopwatchTimestamp - $from.stopwatchTimestamp -ge $from.stopwatchFrequency * 1.1
            } 5
            $seconds = ($to.stopwatchTimestamp - $from.stopwatchTimestamp) / $from.stopwatchFrequency
            [pscustomobject]@{
                requested = $rate
                animationTicksPerSecond = ($to.animationTickCount - $from.animationTickCount) / $seconds
                captionOffsetChanged = $to.captionOffsetCommitCount -gt $from.captionOffsetCommitCount
                spectrumGeometryChanged = $to.spectrumChangedCount -gt $from.spectrumChangedCount
                time = $seconds
            }
        }
        Add-Assertion "broadcast $mode spectrum and long title animate at 30 and 60 FPS" (
            @($cadence | Where-Object {
                $_.captionOffsetChanged -and $_.spectrumGeometryChanged -and
                [math]::Abs($_.requested - $_.animationTicksPerSecond) -le 5
            }).Count -eq 2
        ) $cadence
    }
}

function Test-BroadcastVisibility([object]$Player, [long]$Handle) {
    $title = (Get-Diagnostics).title
    Set-Playback $Player $title 'dynamic.wav' -State 'paused' -Artist 'Rifflo artist'
    $paused = Wait-Diagnostics 'paused playback hides the real window' {
        param($s) $s.state -eq 'Paused' -and !$s.visible -and !$s.preview -and !$s.capturing
    }
    $pausedWindow = Find-RiffloWindow $script:rifflo $script:broadcastWindowTitle -IncludeHidden
    Set-Playback $Player $title 'dynamic.wav' -Artist 'Rifflo artist'
    Wait-Diagnostics 'paused playback resumes' { param($s) $s.visible -and $s.title -eq $title } | Out-Null
    $resumed = Wait-BroadcastWindow $true $true
    Add-Assertion 'paused overlay hides and resumes with the same HWND and title' (
        !$pausedWindow.Visible -and $paused.overlayWindowHandle -eq $Handle -and
        $pausedWindow.Hwnd -eq $Handle -and $resumed.Hwnd -eq $Handle
    ) @{ pause = $pausedWindow; resume = $resumed }

    Invoke-RiffloTray '隐藏悬浮窗'
    Wait-Diagnostics 'tray hides real window and stops drawing' {
        param($s) $s.manuallyHidden -and !$s.visible -and !$s.preview -and !$s.capturing
    } | Out-Null
    $hidden = Find-RiffloWindow $script:rifflo $script:broadcastWindowTitle -IncludeHidden
    Invoke-RiffloTray '恢复悬浮窗'
    Wait-Diagnostics 'tray restores real window' { param($s) $s.visible -and !$s.manuallyHidden } | Out-Null
    $restored = Wait-BroadcastWindow $true $true
    Add-Assertion 'manual hide and restore reuse the same HWND and title' (
        !$hidden.Visible -and $hidden.Hwnd -eq $Handle -and $restored.Hwnd -eq $Handle
    ) @{ hidden = $hidden; restored = $restored }
}

function Get-BroadcastShellEntries([object[]]$Windows) {
    $type = [System.Windows.Automation.AutomationElement]
    foreach ($window in $Windows) {
        try {
            $root = $type::FromHandle([IntPtr]$window.Hwnd)
            if ($null -eq $root) { continue }
            foreach ($element in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
                $name = $element.Current.Name
                if ($name -match 'Rifflo' -and !$element.Current.IsOffscreen) {
                    [pscustomobject]@{ shellClass = $window.ClassName; name = $name; automationId = $element.Current.AutomationId }
                }
            }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
    }
}

function Test-BroadcastShell {
    $overlay = Wait-BroadcastWindow $true $true
    $surface = Start-ClickSurface $overlay 'dark' (Join-Path $script:tempDir 'shell-clicks.txt')
    try {
        $backdrop = Wait-Observation 'shell verification control window' {
            Find-RiffloWindow $surface 'Rifflo verification click surface'
        } { $true }
        [RiffloVerificationNative]::Click(($backdrop.Bounds.Left + 4), ($backdrop.Bounds.Top + 4), $false)
        $taskbar = @([RiffloVerificationNative]::Windows(0) | Where-Object { $_.Visible -and $_.ClassName -in 'Shell_TrayWnd','Shell_SecondaryTrayWnd' })
        $taskbarEntries = @(Get-BroadcastShellEntries $taskbar)
        $before = @([RiffloVerificationNative]::Windows(0) | Where-Object Visible | ForEach-Object Hwnd)
        [RiffloVerificationNative]::SetKey(0x12, $true)
        try {
            [RiffloVerificationNative]::SetKey(0x09, $true)
            [RiffloVerificationNative]::SetKey(0x09, $false)
            $switcher = Wait-Observation 'Windows Alt+Tab switcher' {
                @([RiffloVerificationNative]::Windows(0) | Where-Object {
                    $_.Visible -and $_.Hwnd -notin $before -and
                    $_.ClassName -in 'XamlExplorerHostIslandWindow','MultitaskingViewFrame','TaskSwitcherWnd','Windows.UI.Core.CoreWindow'
                })
            } { param($windows) @($windows).Count -gt 0 } 3
            $switcherEntries = @()
            $controlSeen = $false
            try {
                $switcherEntries = @(Wait-Observation 'Alt+Tab exposes the controlled backdrop window' {
                    Get-BroadcastShellEntries @($switcher)
                } { param($items) @($items | Where-Object name -like '*Rifflo verification click surface*').Count -gt 0 } 3)
                $controlSeen = $true
            } catch {
                $switcherEntries = @(Get-BroadcastShellEntries @($switcher))
            }
            Save-Evidence 'shell-surfaces.json' @{
                overlay = $overlay; taskbarEntries = $taskbarEntries; altTabWindows = @($switcher)
                altTabEntries = $switcherEntries; altTabAccessibleControlSeen = $controlSeen
            }
        } finally {
            [RiffloVerificationNative]::SetKey(0x1B, $true)
            [RiffloVerificationNative]::SetKey(0x1B, $false)
            [RiffloVerificationNative]::SetKey(0x12, $false)
        }
    } finally { Stop-TestProcess $surface }
}

function Invoke-BroadcastVerification {
    $script:rifflo = Start-Rifflo
    Wait-Diagnostics 'idle broadcast verification' { param($s) !$s.visible -and !$s.preview } | Out-Null
    Assert-BroadcastProcessPath

    Invoke-RiffloTray '设置'
    $settings = Get-ProductWindowRoot
    $preview = Wait-Observation 'idle preview HWND' {
        Find-RiffloWindow $script:rifflo 'Rifflo 外观预览'
    } { $true }
    $settingBefore = Get-BroadcastSetting $settings
    $trayBefore = Get-BroadcastTrayState
    Add-Assertion 'new profile starts with both broadcast controls unchecked' (
        !$settingBefore -and $trayBefore -eq $false
    ) @{ settingsChecked = $settingBefore; trayChecked = $trayBefore }

    Set-SettingToggle $settings 'BroadcastCaptureEnabled' $true
    $trayEnabled = Get-BroadcastTrayState
    $previewEnabled = Find-RiffloWindow $script:rifflo 'Rifflo 外观预览'
    Add-Assertion 'settings update tray check and never expose the preview HWND' (
        $trayEnabled -eq $true -and ($previewEnabled.ExtendedStyle -band 0x80) -ne 0 -and
        (Test-BroadcastBounds $preview $previewEnabled)
    ) @{ trayChecked = $trayEnabled; previewBefore = $preview; previewEnabled = $previewEnabled }
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()

    $player = Start-PlaybackFixture 'Broadcast'
    $title = 'That Girl&Talking to the Moon (Full) — Rifflo broadcast fixture long title'
    Set-Playback $player $title 'low.wav' -Artist '4AGX'
    $live = Wait-Diagnostics 'first playback uses live spectrum' {
        param($s) $s.visible -and $s.title -eq $title -and $s.spectrumMode -eq 'Live' -and $s.fftCount -gt 0
    }
    $first = Wait-BroadcastWindow $true $false
    Add-Assertion 'first real HWND is capture eligible when enabled before playback' (
        $first.Hwnd -eq $live.overlayWindowHandle -and $first.Hwnd -ne $preview.Hwnd
    ) $first

    Invoke-RiffloTray '设置'
    $settings = Get-ProductWindowRoot
    $enabledBeforeTrayClick = Get-BroadcastTrayState -Click
    $off = Wait-BroadcastWindow $false $false
    $settingsOff = Get-BroadcastSetting $settings
    $disabledBeforeTrayClick = Get-BroadcastTrayState -Click
    $on = Wait-BroadcastWindow $true $false
    $settingsOn = Get-BroadcastSetting $settings
    Add-Assertion 'tray changes the open settings immediately and keeps window identity and geometry' (
        $enabledBeforeTrayClick -eq $true -and $disabledBeforeTrayClick -eq $false -and !$settingsOff -and $settingsOn -and
        (Test-BroadcastBounds $first $off) -and (Test-BroadcastBounds $first $on)
    ) @{ enabled = $first; disabled = $off; reenabled = $on; settingsOff = $settingsOff; settingsOn = $settingsOn }

    Set-SettingToggle $settings 'PositionLocked' $true
    $locked = Wait-BroadcastWindow $true $true
    Set-SettingToggle $settings 'Topmost' $false
    $withoutTopmost = Wait-BroadcastWindow $true $true $false
    Set-SettingToggle $settings 'BroadcastCaptureEnabled' $false
    $withoutCapture = Wait-BroadcastWindow $false $true $false
    Set-SettingToggle $settings 'BroadcastCaptureEnabled' $true
    $withoutTopmostAgain = Wait-BroadcastWindow $true $true $false
    Set-SettingToggle $settings 'Topmost' $true
    $lockedAgain = Wait-BroadcastWindow $true $true
    Add-Assertion 'position lock and topmost stay independent of broadcast capture on one HWND' (
        (Test-BroadcastBounds $first $locked) -and (Test-BroadcastBounds $first $withoutTopmost) -and
        (Test-BroadcastBounds $first $withoutCapture) -and (Test-BroadcastBounds $first $withoutTopmostAgain) -and
        (Test-BroadcastBounds $first $lockedAgain)
    ) @{ locked = $locked; withoutTopmost = $withoutTopmost; withoutCapture = $withoutCapture; reenabled = $lockedAgain }
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Test-BroadcastClickThrough $lockedAgain

    Invoke-RiffloTray '锁定位置并点击穿透'
    $unlocked = Wait-BroadcastWindow $true $false
    $point = Get-OverlayDragPoint $unlocked (Get-Diagnostics) 'title'
    [RiffloVerificationNative]::Drag($point.x, $point.y, 61, -41)
    $moved = Wait-Observation 'broadcast overlay moves after unlocking' {
        Find-RiffloWindow $script:rifflo $script:broadcastWindowTitle
    } { param($w) $w.Bounds.Left -eq $unlocked.Bounds.Left + 61 -and $w.Bounds.Top -eq $unlocked.Bounds.Top - 41 } 5
    $savedPosition = Wait-SavedPlacement $moved
    Invoke-RiffloTray '锁定位置并点击穿透'
    $relocked = Wait-BroadcastWindow $true $true
    Add-Assertion 'unlocked broadcast overlay can drag, save on release and lock again' (
        (Test-BroadcastBounds $moved $relocked) -and $unlocked.Hwnd -eq $relocked.Hwnd
    ) @{ before = $unlocked; after = $relocked; savedPosition = $savedPosition }

    Invoke-RiffloTray '设置'
    $settings = Get-ProductWindowRoot
    Test-BroadcastAppearance $settings $first.Hwnd $player
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()

    Test-BroadcastVisibility $player $first.Hwnd
    Test-BroadcastShell

    Invoke-RiffloTray '退出'
    if (!$script:rifflo.WaitForExit(7000)) { throw 'The first published process did not exit through the tray.' }
    $saved = Read-JsonSnapshot $script:settingsPath
    Remove-Item -LiteralPath $script:diagPath -ErrorAction SilentlyContinue
    $previousPid = $script:rifflo.Id
    $script:rifflo = Start-Rifflo
    Wait-Diagnostics 'restarted playback preserves the selected manual preset' {
        param($s) $s.visible -and $s.positionLocked -and !$s.liveCaptureEnabled -and $s.spectrumMode -eq 'ManualPreset'
    } | Out-Null
    Assert-BroadcastProcessPath
    $restored = Wait-BroadcastWindow $true $true
    Invoke-RiffloTray '设置'
    $settings = Get-ProductWindowRoot
    Add-Assertion 'broadcast enable, lock and drag position survive normal exit and relaunch' (
        $script:rifflo.Id -ne $previousPid -and $saved.broadcastCaptureEnabled -and !$saved.liveCaptureEnabled -and
        $saved.position.x -eq $restored.Bounds.Left -and $saved.position.y -eq $restored.Bounds.Top -and
        (Get-BroadcastSetting $settings) -and (Get-BroadcastTrayState) -eq $true
    ) @{ previousPid = $previousPid; restored = $restored; settings = $saved }

    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Invoke-RiffloTray '退出'
    if (!$script:rifflo.WaitForExit(7000)) { throw 'The restarted published process did not exit through the tray.' }
}

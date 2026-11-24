function Get-SavedPlacement {
    $saved = Read-JsonSnapshot $script:settingsPath
    if ($null -ne $saved -and $null -ne $saved.PSObject.Properties['position']) { return $saved.position }
}

function Get-OverlayDragPoint([object]$Window, [object]$Snapshot, [string]$Part) {
    $layout = $Snapshot.overlayLayout
    $caption = $layout.caption
    $x = $caption.x + 8
    $y = $caption.y + $caption.height / 2
    if ($Part -eq 'artist') {
        $x = $caption.x + $layout.captionContentWidth - 6
    } elseif ($Part -eq 'spectrum') {
        $spectrum = $layout.spectrum
        $levels = @($Snapshot.bands)
        $strongest = 0
        for ($index = 1; $index -lt $levels.Count; $index++) {
            if ($levels[$index] -gt $levels[$strongest]) { $strongest = $index }
        }
        $gap = [math]::Min(3, $spectrum.width / (2 * $levels.Count))
        $stride = ($spectrum.width + $gap) / $levels.Count
        $x = $spectrum.x + $strongest * $stride + ($stride - $gap) / 2
        $y = $spectrum.y + $spectrum.height - 2
    } elseif ($Part -eq 'blank') {
        $x = 5
        $y = 5
    }
    [pscustomobject]@{
        x = $Window.Bounds.Left + [int]($x * $Window.Dpi / 96.0)
        y = $Window.Bounds.Top + [int]($y * $Window.Dpi / 96.0)
    }
}

function Wait-SavedPlacement([object]$Window) {
    Wait-Observation 'drag writes the final coordinates on release' { Get-SavedPlacement } {
        param($p) $p.x -eq $Window.Bounds.Left -and $p.y -eq $Window.Bounds.Top
    } 5
}

function Test-IdlePreviewPlacement([object]$Settings) {
    $title = 'Rifflo 外观预览'
    $before = Find-RiffloWindow $script:rifflo $title
    if ($null -eq $before) { throw 'Idle preview is unavailable for dragging.' }
    $focusBefore = [RiffloVerificationNative]::KeyboardFocus()
    [RiffloVerificationNative]::Drag(($before.Bounds.Left + 8), ($before.Bounds.Top + 8), 60, -35)
    $after = Wait-Observation 'unlocked idle preview follows the mouse' { Find-RiffloWindow $script:rifflo $title } {
        param($w) $w.Bounds.Left -ne $before.Bounds.Left -and $w.Bounds.Top -ne $before.Bounds.Top
    } 4
    $saved = Wait-SavedPlacement $after
    Add-Assertion 'idle preview uses direct drag and saves without taking keyboard focus' (
        $focusBefore -ne 0 -and $focusBefore -eq [RiffloVerificationNative]::KeyboardFocus() -and $null -ne $saved
    ) @{ before = $before; after = $after; saved = $saved; focus = $focusBefore }
    Invoke-Uia (Get-SettingControl $Settings 'DefaultPosition')
    Wait-Observation 'reset returns the preview to its original default position' { Find-RiffloWindow $script:rifflo $title } {
        param($w) $w.Bounds.Left -eq $before.Bounds.Left -and $w.Bounds.Top -eq $before.Bounds.Top
    } 5 | Out-Null
}

function Get-ClickCount([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return @(Get-Content -LiteralPath $Path).Count }
    return 0
}

function Test-LayoutEdgePlacement([object]$Window, [object]$Snapshot, [object[]]$Monitors) {
    $title = $Window.Title
    $area = ($Monitors | Where-Object {
        $_.Bounds.Left -le $Window.Bounds.Left -and $_.Bounds.Right -gt $Window.Bounds.Left -and
        $_.Bounds.Top -le $Window.Bounds.Top -and $_.Bounds.Bottom -gt $Window.Bounds.Top
    } | Select-Object -First 1).Work

    $width = $Window.Bounds.Right - $Window.Bounds.Left
    $target = $area.Right - $width - 8
    $point = Get-OverlayDragPoint $Window $Snapshot 'blank'
    [RiffloVerificationNative]::Drag($point.x, $point.y, ($target - $Window.Bounds.Left), 0)
    $atEdge = Wait-Observation 'drag places the overlay beside the right work area edge' {
        Find-RiffloWindow $script:rifflo $title
    } { param($w) $w.Bounds.Left -eq $target } 5
    $anchor = Wait-SavedPlacement $atEdge

    Invoke-RiffloTray '设置'
    $settings = Get-ProductWindowRoot
    Select-SettingOption $settings 'Layout' '左歌名、右频谱'
    $expanded = Wait-Observation 'horizontal layout widens the overlay near the edge' {
        Find-RiffloWindow $script:rifflo $title
    } { param($w) ($w.Bounds.Right - $w.Bounds.Left) -gt $width + 15 } 5
    $expandedWidth = $expanded.Bounds.Right - $expanded.Bounds.Left
    $expectedLeft = [math]::Min($atEdge.Bounds.Left, $area.Right - $expandedWidth)
    Add-Assertion 'larger layout near a work-area edge uses only the necessary translation' (
        $expanded.Bounds.Left -lt $atEdge.Bounds.Left -and
        [math]::Abs($expanded.Bounds.Left - $expectedLeft) -le 1 -and $expanded.Bounds.Top -eq $atEdge.Bounds.Top
    ) @{ anchor = $anchor; work = $area; expectedLeft = $expectedLeft; expanded = $expanded }

    Set-SettingNumber $settings 'TextWidth' 320
    Select-SettingOption $settings 'ScrollMode' '左右往返'
    Set-SettingToggle $settings 'PositionLocked' $true
    $options = Wait-Observation 'appearance and lock are written before reset' { Read-JsonSnapshot $script:settingsPath } {
        param($s) $null -ne $s.position -and $s.positionLocked -and $s.textWidth -eq 320 -and $s.scrollMode -eq 1 -and $s.layout -eq 2
    } 5
    Invoke-Uia (Get-SettingControl $settings 'DefaultPosition')
    $reset = Wait-Observation 'reset clears only the saved position' { Read-JsonSnapshot $script:settingsPath } {
        param($s) $null -eq $s.position
    } 5
    $primary = $Monitors | Where-Object Primary | Select-Object -First 1
    $resetWindow = Wait-Observation 'reset returns to the primary work area lower-left' {
        Find-RiffloWindow $script:rifflo $title
    } {
        param($w)
        $margin = [int](24 * $w.Dpi / 96.0)
        [math]::Abs($w.Bounds.Left - $primary.Work.Left - $margin) -le 1 -and
            [math]::Abs($primary.Work.Bottom - $w.Bounds.Bottom - $margin) -le 1
    } 5
    Add-Assertion 'return to lower-left preserves the selected layout, width, scroll mode and lock' (
        $reset.layout -eq $options.layout -and $reset.textWidth -eq $options.textWidth -and
        $reset.scrollMode -eq $options.scrollMode -and $reset.positionLocked -and $reset.topmost -eq $options.topmost
    ) @{ previous = $options; reset = $reset; window = $resetWindow }

    Set-SettingToggle $settings 'PositionLocked' $false
    Set-SettingNumber $settings 'TextWidth' 260
    Select-SettingOption $settings 'Layout' '上频谱、下歌名'
    Select-SettingOption $settings 'ScrollMode' '单向循环'
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
}

function Invoke-PlacementVerification([switch]$CrossMonitor) {
    $windowTitle = 'Rifflo 桌面悬浮窗'
    $lockLabel = '锁定位置并点击穿透'
    $initial = Get-Diagnostics
    $trackTitle = $initial.title
    $overlay = Find-RiffloWindow $script:rifflo $windowTitle
    $monitors = @([RiffloVerificationNative]::Monitors())
    $primary = $monitors | Where-Object Primary | Select-Object -First 1
    if ($null -eq $overlay -or $null -eq $primary) { throw 'Desktop window or primary monitor is unavailable.' }
    $margin = [int](24 * $overlay.Dpi / 96.0)
    $left = $overlay.Bounds.Left - $primary.Work.Left
    $bottom = $primary.Work.Bottom - $overlay.Bounds.Bottom
    Add-Assertion 'fresh playback overlay uses the primary work area and 24 DIP margins' (
        [math]::Abs($left - $margin) -le 1 -and [math]::Abs($bottom - $margin) -le 1
    ) @{ leftPx = $left; bottomPx = $bottom; expectedPx = $margin; dpi = $overlay.Dpi; work = $primary.Work }
    Add-Assertion 'fresh overlay is layered, non-activating, and draggable by default' (
        ($overlay.ExtendedStyle -band 0x080800A0) -eq 0x08080080 -and !$initial.positionLocked
    ) $overlay

    $clickLog = Join-Path $script:tempDir 'placement-clicks.txt'
    $focusSurface = Start-ClickSurface $overlay 'dark' $clickLog
    try {
        $backdrop = Wait-Observation 'foreground surface for focus verification' {
            Find-RiffloWindow $focusSurface 'Rifflo verification click surface'
        } { $true }
        [RiffloVerificationNative]::Click(($backdrop.Bounds.Left + 4), ($backdrop.Bounds.Top + 4), $false)
        $active = [RiffloVerificationNative]::Foreground()
        $focus = [RiffloVerificationNative]::KeyboardFocus()
        $oldCursor = [RiffloVerificationNative]::Cursor()
        $point = Get-OverlayDragPoint $overlay $initial 'title'
        $before = Get-SavedPlacement
        [RiffloVerificationNative]::BeginDrag($point.x, $point.y)
        try {
            Start-Sleep -Milliseconds 65
            [RiffloVerificationNative]::MovePointer(($point.x + 70), ($point.y - 45))
            $held = Wait-Observation 'window follows the pointer before release' { Find-RiffloWindow $script:rifflo $windowTitle } {
                param($w) $w.Bounds.Left -eq $overlay.Bounds.Left + 70 -and $w.Bounds.Top -eq $overlay.Bounds.Top - 45
            } 4
            Start-Sleep -Milliseconds 410
            $whileHeld = Get-SavedPlacement
            Add-Assertion 'holding the title drag leaves the saved position unchanged' (
                ($before | ConvertTo-Json -Compress) -eq ($whileHeld | ConvertTo-Json -Compress)
            ) @{ before = $before; held = $held; savedWhileHeld = $whileHeld }
        } finally {
            [RiffloVerificationNative]::ReleaseDrag()
            Start-Sleep -Milliseconds 120
            [RiffloVerificationNative]::MovePointer($oldCursor.X, $oldCursor.Y)
        }
        $saved = Wait-SavedPlacement $held
        $focusAfter = [RiffloVerificationNative]::KeyboardFocus()
        $activeAfter = [RiffloVerificationNative]::Foreground()
        Add-Assertion 'moving the title saves on release and keeps the foreground keyboard focus' (
            $active.ProcessId -eq $focusSurface.Id -and $activeAfter.Hwnd -eq $active.Hwnd -and $focus -ne 0 -and $focusAfter -eq $focus
        ) @{ before = $overlay; after = $held; saved = $saved; focusBefore = $focus; focusAfter = $focusAfter }
    } finally { Stop-TestProcess $focusSurface }

    foreach ($part in @('artist', 'spectrum', 'blank')) {
        $before = Find-RiffloWindow $script:rifflo $windowTitle
        $point = Get-OverlayDragPoint $before (Get-Diagnostics) $part
        [RiffloVerificationNative]::Drag($point.x, $point.y, 61, -30)
        $after = Wait-Observation "overlay drag from $part" { Find-RiffloWindow $script:rifflo $windowTitle } {
            param($w) $w.Bounds.Left -eq $before.Bounds.Left + 61 -and $w.Bounds.Top -eq $before.Bounds.Top - 30
        } 4
        $saved = Wait-SavedPlacement $after
        Add-Assertion "physical drag from $part moves and saves the whole overlay" ($null -ne $saved) @{ pointer = $point; before = $before; after = $after; saved = $saved }
    }

    Invoke-RiffloTray '设置'
    $settings = Get-ProductWindowRoot
    Set-SettingToggle $settings 'PositionLocked' $true
    Set-SettingToggle $settings 'Topmost' $false
    $locked = Find-RiffloWindow $script:rifflo $windowTitle
    Add-Assertion 'position lock and topmost settings can be changed independently' (
        ($locked.ExtendedStyle -band 0x28) -eq 0x20
    ) $locked
    Set-SettingToggle $settings 'Topmost' $true
    $settings.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    $sample = Wait-Diagnostics 'pointer passes through the locked overlay' { param($s) $s.positionLocked }
    $overlay = Find-RiffloWindow $script:rifflo $windowTitle
    $surfaces = @(foreach ($shade in @('light', 'dark')) {
        $surface = Start-ClickSurface $overlay $shade $clickLog
        try {
            Wait-Observation "$shade backing window" { Find-RiffloWindow $surface 'Rifflo verification click surface' } { $true } | Out-Null
            $point = Get-OverlayDragPoint $overlay $sample 'title'
            $hit = [RiffloVerificationNative]::WindowAt($point.x, $point.y)
            $clicks = Get-ClickCount $clickLog
            [RiffloVerificationNative]::Click($point.x, $point.y, $false)
            $received = Wait-Observation "$shade test surface records the click" { Get-ClickCount $clickLog } { param($n) $n -gt $clicks } 4
            $focusAfter = [RiffloVerificationNative]::Foreground()
            $bounds = $overlay.Bounds
            $image = "overlay-$shade.png"
            Save-ScreenCrop $image ($bounds.Left - 8) ($bounds.Top - 8) ($bounds.Right - $bounds.Left + 16) ($bounds.Bottom - $bounds.Top + 16)
            $pixels = Read-OverlayPixels $image ($bounds.Left - 8)
            Add-Assertion "$shade surface receives a physical click through the locked overlay" (
                $hit.ProcessId -eq $surface.Id -and $focusAfter.ProcessId -eq $surface.Id -and $received -eq $clicks + 1
            ) @{ expectedPid = $surface.Id; hitPid = $hit.ProcessId; foregroundPid = $focusAfter.ProcessId; pixels = $pixels }
            [pscustomobject]@{ shade = $shade; image = $image; pixels = $pixels }
        } finally { Stop-TestProcess $surface }
    })
    Add-Assertion 'live spectrum remains visible on light and dark backdrops' (
        @($surfaces | Where-Object { $_.pixels.spectrumPixels -gt 20 }).Count -eq 2
    ) $surfaces

    $point = Get-OverlayDragPoint $overlay $sample 'blank'
    [RiffloVerificationNative]::Drag($point.x, $point.y, 62, -41)
    $attempted = Find-RiffloWindow $script:rifflo $windowTitle
    Add-Assertion 'locked overlay stays put during a drag gesture' (
        $attempted.Bounds.Left -eq $overlay.Bounds.Left -and $attempted.Bounds.Top -eq $overlay.Bounds.Top
    ) @{ locked = $overlay; attempted = $attempted }
    Invoke-RiffloTray $lockLabel
    $unlocked = Wait-Diagnostics 'tray releases click-through lock' { param($s) !$s.positionLocked }
    $point = Get-OverlayDragPoint $attempted $unlocked 'blank'

    if ($CrossMonitor) {
        $secondary = $monitors | Where-Object { !$_.Primary } | Sort-Object { $_.Bounds.Left } | Select-Object -First 1
        if ($null -eq $secondary) { throw 'CrossMonitor requires a secondary display.' }
        $targetX = [int](($secondary.Work.Left + $secondary.Work.Right) / 2)
        $targetY = [int](($secondary.Work.Top + $secondary.Work.Bottom) / 2)
        [RiffloVerificationNative]::Drag($point.x, $point.y, ($targetX - $point.x), ($targetY - $point.y))
        $moved = Wait-Observation 'unlocked overlay crosses to the secondary work area' { Find-RiffloWindow $script:rifflo $windowTitle } {
            param($w) $w.Bounds.Left -ge $secondary.Work.Left -and $w.Bounds.Top -ge $secondary.Work.Top -and
                $w.Bounds.Right -le $secondary.Work.Right -and $w.Bounds.Bottom -le $secondary.Work.Bottom
        } 5
        Save-Evidence 'cross-monitor.json' @{ original = $attempted; final = $moved; secondary = $secondary }
        Add-Assertion 'physical cross-monitor drag immediately works after unlocking via the tray' (
            ($moved.ExtendedStyle -band 0x20) -eq 0 -and $moved.Bounds.Left -ne $attempted.Bounds.Left
        ) @{ originalDpi = $attempted.Dpi; finalDpi = $moved.Dpi; finalX = $moved.Bounds.Left; finalY = $moved.Bounds.Top }
        if ($moved.Dpi -ne $attempted.Dpi) {
            $originalWidthDip = ($attempted.Bounds.Right - $attempted.Bounds.Left) * 96.0 / $attempted.Dpi
            $finalWidthDip = ($moved.Bounds.Right - $moved.Bounds.Left) * 96.0 / $moved.Dpi
            Add-Assertion 'different display DPI rescales the overlay while preserving its layout width in DIPs' (
                [math]::Abs($originalWidthDip - $finalWidthDip) -le 2
            ) @{ originalDpi = $attempted.Dpi; finalDpi = $moved.Dpi; originalWidthDip = $originalWidthDip; finalWidthDip = $finalWidthDip }
        }
    } else {
        [RiffloVerificationNative]::Drag($point.x, $point.y, 62, -41)
        $moved = Wait-Observation 'overlay moves as soon as tray unlocks it' { Find-RiffloWindow $script:rifflo $windowTitle } {
            param($w) $w.Bounds.Left -eq $attempted.Bounds.Left + 62 -and $w.Bounds.Top -eq $attempted.Bounds.Top - 41
        } 5
        Add-Assertion 'physical drag immediately works after unlocking via the tray' (($moved.ExtendedStyle -band 0x20) -eq 0) $moved
    }

    $saved = Wait-SavedPlacement $moved
    Invoke-RiffloTray '退出'
    if (!$script:rifflo.WaitForExit(7000)) { throw 'Published application did not exit via the tray after a drag.' }
    Remove-Item -LiteralPath $script:diagPath -ErrorAction SilentlyContinue
    $script:rifflo = Start-Rifflo
    $restored = Wait-Diagnostics 'fresh process restores the last released position' {
        param($s) $s.visible -and $s.capturing -and $s.title -eq $trackTitle
    }
    Add-Assertion 'last released drag survives immediate normal exit and relaunch' (
        $restored.x -eq $saved.x -and $restored.y -eq $saved.y
    ) @{ saved = $saved; restored = @($restored.x, $restored.y) }
    Save-Evidence 'desktop-placement.json' @{ initial = $initial; saved = $saved; restored = $restored; backdrops = $surfaces }
    Test-LayoutEdgePlacement (Find-RiffloWindow $script:rifflo $windowTitle) $restored $monitors
}

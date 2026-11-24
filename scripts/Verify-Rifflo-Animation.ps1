#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$PublishDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x64-animation-verified'),
    [string]$EvidenceDirectory,
    [ValidateRange(60, 1800)][int]$PhaseSeconds = 62,
    [ValidateRange(6, 30)][int]$ProbeSeconds,
    [ValidateSet('live', 'preset', 'marquee', 'hidden', 'static', 'idle')]
    [string[]]$Scenarios = @('live', 'preset', 'marquee', 'static', 'hidden', 'idle'),
    [ValidateSet(30, 60)][int[]]$RateOrder,
    [switch]$ReverseOrder,
    [switch]$SkipDesktopChecks,
    [switch]$DesktopChecksOnly,
    [switch]$DesktopPixelsOnly,
    [ValidateSet('Auto','DXGI','GDI')][string]$DesktopCaptureMethod = 'Auto',
    [switch]$Preflight
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Rifflo.Verification.Helpers.ps1')

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (!$EvidenceDirectory) {
    $EvidenceDirectory = Join-Path $repo ('artifacts\verification\animation-framerate-20261124\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$script:ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:assertions = [Collections.Generic.List[object]]::new()
$script:summaries = [Collections.Generic.List[object]]::new()
$script:tempDir = Join-Path ([IO.Path]::GetTempPath()) ('RiffloAnimation-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:tempDir | Out-Null
$script:settingsPath = Join-Path $script:tempDir 'settings.json'
$script:diagPath = Join-Path $script:tempDir 'diagnostics.json'
$script:exe = [IO.Path]::GetFullPath((Join-Path $PublishDirectory 'Rifflo.exe'))
$script:rifflo = $null
$script:player = $null
$script:monitorEvidence = $null
$script:label30 = '30 帧（默认）'
$script:label60 = '60 帧'
$script:shortTitle = 'Rifflo'
# More than 60 seconds of uninterrupted travel at 30 DIP/s in the 260 DIP viewport.
$script:longTitle = ('Rifflo ' + ('Waves in motion / ' * 26)).Trim()
$script:clockFrequency = [Diagnostics.Stopwatch]::Frequency
$Mode = 'Animation'

function Start-AnimationApp {
    Remove-Item -LiteralPath $script:diagPath -ErrorAction SilentlyContinue
    $previous = $env:RIFFLO_FRAME_TRACE
    try {
        $env:RIFFLO_FRAME_TRACE = '1'
        $script:rifflo = Start-Rifflo
    } finally {
        $env:RIFFLO_FRAME_TRACE = $previous
    }
    Wait-Diagnostics 'published application diagnostic PID' { param($s) $s.processId -eq $script:rifflo.Id } | Out-Null
    if ($script:rifflo.Path -ne $script:exe) { throw 'The running process does not match the published executable.' }
}

function Stop-AnimationApp {
    Invoke-RiffloTray '退出'
    if (!$script:rifflo.WaitForExit(5000) -or $script:rifflo.ExitCode -ne 0) {
        throw 'The publish process did not exit normally through its tray menu.'
    }
}

function Get-OwnedMenuItem([string]$Label) {
    Wait-Observation "test tray item $Label" {
        $windows = @([RiffloVerificationNative]::Windows($script:rifflo.Id) | Where-Object Visible)
        Find-UiaInWindows $windows $Label
    } { $true } 3
}

function Click-OwnedMenuItem([object]$Item) {
    $point = Get-UiaClickPoint $Item
    if ([RiffloVerificationNative]::WindowAt($point.x, $point.y).ProcessId -ne $script:rifflo.Id) {
        throw "The test instance's tray menu item is obscured."
    }
    [RiffloVerificationNative]::Click($point.x, $point.y, $false)
}

function Get-MenuCheck([object]$Item) {
    $pattern = $null
    if ($Item.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) {
        return ($pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
    }
    $point = Get-UiaClickPoint $Item
    [RiffloVerificationNative]::MenuItemChecked($point.x, $point.y)
}

function Set-TrayAnimationRate([int]$Rate, [bool]$Click = $true, [string]$ImageName) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $icon = Find-TrayRifflo
            $point = Get-UiaClickPoint $icon
            [RiffloVerificationNative]::Click($point.x, $point.y, $true)
            Click-OwnedMenuItem (Get-OwnedMenuItem '悬浮窗动画帧率')
            $low = Get-OwnedMenuItem $script:label30
            $high = Get-OwnedMenuItem $script:label60
            break
        } catch {
            if ($attempt -eq 3) { throw }
        }
    }
    $checks = [pscustomobject]@{
        low = Get-MenuCheck $low
        high = Get-MenuCheck $high
        timestamp = [Diagnostics.Stopwatch]::GetTimestamp()
    }
    if ($ImageName) {
        $upper = $low.Current.BoundingRectangle
        $lower = $high.Current.BoundingRectangle
        Save-ScreenCrop $ImageName ([int]$upper.Left) ([int]$upper.Top) ([int]$upper.Width) ([int]($lower.Bottom - $upper.Top))
    }
    # Selecting the already-checked entry also dismisses the real tray menu.
    $selected = if ($Rate -eq 30) { $low } else { $high }
    Click-OwnedMenuItem $selected
    if ($Click) {
        Wait-Diagnostics "$Rate fps applied" { param($s) $s.animationFrameRate -eq $Rate } | Out-Null
    }
    $checks
}

function Open-AnimationSettings {
    Invoke-RiffloTray '设置'
    $window = Wait-Observation 'published Rifflo settings' { Find-RiffloWindow $script:rifflo 'Rifflo 设置' } { $true }
    [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$window.Hwnd)
}

function Close-AnimationSettings([object]$Window) {
    $Window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
    Wait-Diagnostics 'settings and preview closed' { param($s) !$s.settingsVisible -and !$s.preview } | Out-Null
}

function Start-AnimationPlayback([string]$Title) {
    if ($null -eq $script:player -or $script:player.process.HasExited) {
        $script:player = Start-PlaybackFixture ('Animation' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    }
    Set-Playback $script:player $Title 'dynamic.wav' -Artist ''
    Wait-Diagnostics 'selected real fixture GSMTC track' {
        param($s) $s.title -eq $Title -and $s.source -eq $script:player.aumid -and $s.state -eq 'Playing'
    } | Out-Null
}

function Stop-AnimationPlayback {
    if ($null -ne $script:player -and !$script:player.process.HasExited) {
        Set-Playback $script:player 'Rifflo' 'dynamic.wav' 'exit' -NoWait
        if (!$script:player.process.WaitForExit(5000)) { throw 'The playback fixture did not exit on request.' }
    }
    $script:player = $null
    Wait-Diagnostics 'no accepted media session' { param($s) !$s.title -and !$s.visible -and !$s.preview } | Out-Null
}

function Get-OverlayMonitor([object]$Window) {
    $bounds = $Window.Bounds
    $midX = ($bounds.Left + $bounds.Right) / 2
    $midY = ($bounds.Top + $bounds.Bottom) / 2
    $monitor = [RiffloVerificationNative]::Monitors() | Where-Object {
        $midX -ge $_.Bounds.Left -and $midX -lt $_.Bounds.Right -and
        $midY -ge $_.Bounds.Top -and $midY -lt $_.Bounds.Bottom
    } | Select-Object -First 1
    if ($null -eq $monitor -or $monitor.RefreshHz -lt 60) { throw 'The overlay must be on a detected display at 60 Hz or higher.' }
    [pscustomobject]@{
        monitor = $monitor
        dpi = $Window.Dpi
        scalePercent = 100 * $Window.Dpi / 96
        overlayPhysicalBounds = $bounds
        overlayHwnd = $Window.Hwnd
    }
}

function Set-AnimationScenario([string]$Name) {
    $current = Get-Diagnostics
    if ($current.manuallyHidden) {
        Invoke-RiffloTray '恢复悬浮窗'
        Wait-Diagnostics 'manual hide cleared' { param($s) !$s.manuallyHidden } | Out-Null
    }
    if ($Name -eq 'idle') {
        Stop-AnimationPlayback
        return
    }
    $enableSpectrum = $Name -in @('live', 'preset', 'hidden')
    $enableLive = $Name -ne 'preset'
    $current = Get-Diagnostics
    if ($current.spectrumEnabled -ne $enableSpectrum -or $current.liveCaptureEnabled -ne $enableLive) {
        $settings = Open-AnimationSettings
        Set-SettingToggle $settings 'SpectrumEnabled' $enableSpectrum
        if ($enableSpectrum) {
            Set-SettingToggle $settings 'LiveCaptureEnabled' $enableLive
        }
        Close-AnimationSettings $settings
    }
    $title = if ($Name -eq 'marquee') { $script:longTitle } else { $script:shortTitle }
    Start-AnimationPlayback $title
    Wait-Diagnostics 'benchmark overlay visible without settings or preview' {
        param($s) $s.visible -and !$s.preview -and !$s.settingsVisible -and $s.spectrumEnabled -eq $enableSpectrum
    } | Out-Null
    switch ($Name) {
        'live' { Wait-Diagnostics 'dynamic live process audio' { param($s) $s.spectrumMode -eq 'Live' -and $s.captureRunning } | Out-Null }
        'preset' { Wait-Diagnostics 'manual spectrum without capture' { param($s) $s.spectrumMode -eq 'ManualPreset' -and !$s.captureRunning } | Out-Null }
        'hidden' {
            Wait-Diagnostics 'playing live before hiding' { param($s) $s.spectrumMode -eq 'Live' -and $s.captureRunning } | Out-Null
            Invoke-RiffloTray '隐藏悬浮窗'
            Wait-Diagnostics 'hidden but still playing' { param($s) $s.manuallyHidden -and !$s.visible -and !$s.preview -and $s.state -eq 'Playing' } | Out-Null
        }
    }
    if ($Name -in 'live','preset','static') {
        $ready = Get-Diagnostics
        if ($ready.overlayLayout.captionScrolling -or $ready.overlayLayout.captionContentWidth -gt $ready.overlayLayout.caption.width) {
            throw "The controlled short title does not fit for $Name."
        }
    }
}

function Measure-DesktopPixels([string]$Scene, [int]$Rate) {
    $snapshot = Get-Diagnostics
    $window = Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗'
    $bounds = $window.Bounds
    $background = Register-TestProcess (Start-Process -FilePath $script:fixture -WindowStyle Hidden -PassThru -ArgumentList @(
        '--surface', ($bounds.Left - 10), ($bounds.Top - 10),
        ($bounds.Right - $bounds.Left + 20), ($bounds.Bottom - $bounds.Top + 20), 'dark',
        ('"{0}"' -f (Join-Path $script:tempDir 'pixel-background-clicks.txt'))
    ))
    try {
        Wait-Observation 'owned solid background below transparent overlay' {
            Find-RiffloWindow $background 'Rifflo verification click surface'
        } { $true } | Out-Null
        Start-Sleep -Seconds 2
        $region = if ($Scene -in 'live','preset') { $snapshot.overlayLayout.spectrum } else { $snapshot.overlayLayout.caption }
        $scale = $window.Dpi / 96
        $x = [int][math]::Floor($bounds.Left + $region.x * $scale)
        $y = [int][math]::Floor($bounds.Top + $region.y * $scale)
        $width = [int][math]::Ceiling($region.width * $scale)
        $height = [int][math]::Ceiling($region.height * $scale)
        $hit = [RiffloVerificationNative]::WindowAt(($x + [int]($width / 2)), ($y + [int]($height / 2)))
        if ($hit.ProcessId -notin $script:rifflo.Id, $background.Id) { throw 'The controlled overlay crop is obscured.' }
        Save-ScreenCrop "$Scene-$Rate.png" $x $y $width $height
        $capture = $null
        $captureError = $null
        $monitor = (Get-OverlayMonitor $window).monitor
        if ($DesktopCaptureMethod -ne 'GDI') {
            try {
                $capture = [RiffloDesktopDuplication]::Read($monitor.Device, $x, $y, $width, $height, 8)
            } catch {
                if ($DesktopCaptureMethod -eq 'DXGI') { throw }
                $captureError = $_.Exception.GetBaseException().Message
                Write-Warning "$Scene/$Rate Desktop Duplication unavailable. $captureError"
            }
        }
        if ($null -ne $capture) {
            $samples = @($capture.Samples | Where-Object { $_.Timestamp -ge $capture.Start -and $_.Timestamp -le $capture.End })
            $start = $capture.Start
            $end = $capture.End
        } else {
            $start = [Diagnostics.Stopwatch]::GetTimestamp()
            $samples = [RiffloVerificationNative]::SampleDesktopPixels($x, $y, $width, $height, 8)
            $end = [Diagnostics.Stopwatch]::GetTimestamp()
        }
        $shapeChanges = @(for ($index = 1; $index -lt $samples.Length; $index++) {
            if ($samples[$index].RgbHash -ne $samples[$index - 1].RgbHash) {
                [pscustomobject]@{ timestamp = $samples[$index].Timestamp; value = 0 }
            }
        })
        $elapsed = ($end - $start) / $script:clockFrequency
        if ($elapsed -le 0) { throw 'The composed desktop sample had no measurable time span.' }
        $stats = Get-TraceStats $shapeChanges $elapsed $Rate
        $geometrySnapshot = Get-Diagnostics
        $geometryTrace = if ($Scene -in 'live','preset') { $geometrySnapshot.spectrumFrames } else { $geometrySnapshot.captionFrames }
        $geometryTrace = @($geometryTrace | Where-Object { $_.timestamp -ge $start -and $_.timestamp -le $end })
        $summary = [pscustomobject]@{
            scene = $Scene; rate = $Rate; sampleSeconds = $elapsed; acquiredDesktopImagesPerSecond = $samples.Length / $elapsed
            captureMethod = if ($null -ne $capture) { 'DXGI Desktop Duplication' } else { 'GDI DwmFlush + BitBlt' }
            desktopUpdateTimesFromQpc = $null -ne $capture
            captureGpuAdapterLuid = if ($null -ne $capture) { $capture.GpuAdapterLuid } else { $null }
            captureFailure = $captureError
            accumulatedFrames = if ($null -ne $capture) { @($samples | Where-Object AccumulatedFrames -GT 1).Count } else { $null }
            timeoutCount = if ($null -ne $capture) { $capture.TimeoutCount } else { $null }
            pixelHz = $stats.effectiveHz; pixelCount = $stats.count; p50Ms = $stats.p50Ms; p95Ms = $stats.p95Ms
            geometryTraceOverlap = $geometryTrace.Count; overlayWindow = $window
            bounds = @{ x = $x; y = $y; width = $width; height = $height }
        }
        Save-Evidence "$Scene-$Rate.pixels.json" @{ phase = $summary; samples = $samples; recentGeometry = $geometryTrace }
        Write-Host ('PIXELS {0}/{1} {2:N2} Hz, p95 {3:N2} ms at {4:N2} samples/s by {5} and {6} accumulated samples' -f $Scene, $Rate, $summary.pixelHz, $summary.p95Ms, $summary.acquiredDesktopImagesPerSecond, $summary.captureMethod, $summary.accumulatedFrames)
        $summary
    } finally {
        Stop-TestProcess $background
    }
}

function Get-GpuRaw([Diagnostics.Process]$Process) {
    try {
        Get-CimInstance -ClassName Win32_PerfRawData_GPUPerformanceCounters_GPUEngine -Filter "Name LIKE 'pid_$($Process.Id)_%'" |
            Where-Object Name -Like "pid_$($Process.Id)_*" | Sort-Object Name |
            Select-Object Name, RunningTime, Timestamp_Sys100NS
    } catch {
        Write-Verbose "GPU performance counters unavailable: $($_.Exception.Message)"
    }
}

function Get-GpuPhase([object[]]$Rows) {
    $first = $Rows[0].gpu
    $last = $Rows[-1].gpu
    if ($first.Count -eq 0 -or $last.Count -eq 0) {
        return [pscustomobject]@{ quality = 'No PID engine instances at a sample boundary'; engines = @() }
    }
    $engineEstimates = @(foreach ($engine in $first) {
        $end = $last | Where-Object Name -EQ $engine.Name | Select-Object -First 1
        if ($null -eq $end) { continue }
        $wall = [decimal]$end.Timestamp_Sys100NS - [decimal]$engine.Timestamp_Sys100NS
        $running = [decimal]$end.RunningTime - [decimal]$engine.RunningTime
        if ($wall -le 0 -or $running -lt 0 -or $running -gt $wall) { continue }
        $name = $engine.Name -replace '^pid_\d+_', ''
        $consistent = @($Rows | Where-Object { $engine.Name -notin @($_.gpu | ForEach-Object Name) }).Count -eq 0
        if ($consistent) {
            [pscustomobject]@{ engine = $name; meanPercent = [double](100 * $running / $wall); timeSeconds = [double]($wall / 10000000) }
        }
    })
    $missing = @($Rows | Where-Object { ((@($_.gpu | ForEach-Object Name)) -join '|') -ne ($first.Name -join '|') }).Count
    [pscustomobject]@{
        quality = if ($missing -eq 0 -and $engineEstimates.Count -eq $first.Count) { 'Continuous exact PID engine instances' } else { 'PID engine instance set varied' }
        engines = @($engineEstimates | Sort-Object meanPercent -Descending)
    }
}

function Get-Quantile([double[]]$Numbers, [double]$Fraction) {
    if ($Numbers.Count -eq 0) { return $null }
    $sorted = @($Numbers | Sort-Object)
    $index = ($sorted.Count - 1) * $Fraction
    $low = [int][math]::Floor($index)
    $high = [int][math]::Ceiling($index)
    $sorted[$low] + ($index - $low) * ($sorted[$high] - $sorted[$low])
}

function Get-TraceStats([object[]]$Frames, [double]$Elapsed, [int]$Rate, [switch]$Marquee) {
    $gaps = [Collections.Generic.List[double]]::new()
    $steps = [Collections.Generic.List[double]]::new()
    $segments = [Collections.Generic.List[object]]::new()
    $start = 0
    for ($index = 1; $index -lt $Frames.Count; $index++) {
        $gap = 1000 * ($Frames[$index].timestamp - $Frames[$index - 1].timestamp) / $script:clockFrequency
        $step = $Frames[$index].value - $Frames[$index - 1].value
        if ($Marquee -and ($gap -ge 1000 -or [math]::Abs($step) -gt 120)) {
            $segments.Add([pscustomobject]@{ start = $start; end = $index - 1; gapMs = $gap })
            $start = $index
        } else {
            $gaps.Add($gap)
            $steps.Add($step)
        }
    }
    if ($Frames.Count -gt 0) {
        $segments.Add([pscustomobject]@{ start = $start; end = $Frames.Count - 1; gapMs = 0 })
    }
    $displacement = $null
    $movingRate = $null
    $distanceAt30s = $null
    $movingSeconds = 0.0
    if ($Marquee -and $segments.Count -gt 0) {
        $longest = $segments | Sort-Object { $_.end - $_.start } -Descending | Select-Object -First 1
        $first = $Frames[$longest.start]
        $last = $Frames[$longest.end]
        $movingSeconds = ($last.timestamp - $first.timestamp) / $script:clockFrequency
        if ($movingSeconds -gt 0) {
            $movingRate = ($longest.end - $longest.start) / $movingSeconds
            $displacement = ($last.value - $first.value) / $movingSeconds
            $target = $first.timestamp + $script:clockFrequency * 30
            for ($index = $longest.start + 1; $index -le $longest.end; $index++) {
                if ($Frames[$index].timestamp -lt $target) { continue }
                $before = $Frames[$index - 1]
                $after = $Frames[$index]
                $fraction = ($target - $before.timestamp) / ($after.timestamp - $before.timestamp)
                $distanceAt30s = $before.value + $fraction * ($after.value - $before.value) - $first.value
                break
            }
        }
    }
    [pscustomobject]@{
        count = $Frames.Count
        effectiveHz = $Frames.Count / $Elapsed
        effectiveMovingHz = $movingRate
        movingSeconds = $movingSeconds
        p50Ms = Get-Quantile $gaps.ToArray() 0.50
        p95Ms = Get-Quantile $gaps.ToArray() 0.95
        maxMs = if ($gaps.Count -gt 0) { ($gaps | Measure-Object -Maximum).Maximum } else { $null }
        delayedIntervals = @($gaps | Where-Object { $_ -gt (2 * 1000 / $Rate) }).Count
        medianStepDip = Get-Quantile $steps.ToArray() 0.50
        speedDipPerSecond = $displacement
        distanceAt30sDip = $distanceAt30s
        segments = $segments.ToArray()
    }
}

function Measure-AnimationPhase([string]$Name, [int]$Rate, [int]$Seconds) {
    $phase = "$Name-$Rate"
    Write-Host ("MEASURE {0} [{1}s] {2}" -f $phase, $Seconds, (Get-Date -Format HH:mm:ss))
    $diagWriter = [IO.StreamWriter]::new((Join-Path $EvidenceDirectory "$phase.diagnostics.jsonl"))
    $resources = [Collections.Generic.List[object]]::new()
    $diagnostics = [Collections.Generic.List[object]]::new()
    $spectrumFrames = [Collections.Generic.SortedDictionary[long,object]]::new()
    $captionFrames = [Collections.Generic.SortedDictionary[long,object]]::new()
    $prevSnapshot = 0L
    $longestSnapshotGap = 0.0
    $start = 0L
    $end = 0L
    $nextResource = 0L
    try {
        do {
            $now = [Diagnostics.Stopwatch]::GetTimestamp()
            if ($now -ge $nextResource) {
                $script:rifflo.Refresh()
                if ($script:rifflo.HasExited) { throw 'The benchmark process exited before sampling ended.' }
                $cpu = $script:rifflo.TotalProcessorTime.TotalSeconds
                if (!$start) { $start = $now }
                $end = $now
                $memory = Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "IDProcess = $($script:rifflo.Id)"
                if ($null -eq $memory) { throw 'Private working-set process counter unavailable.' }
                $resources.Add([pscustomobject]@{
                    timestamp = $now
                    utc = [DateTimeOffset]::UtcNow.ToString('O')
                    pid = $script:rifflo.Id
                    cpuSeconds = $cpu
                    privateWorkingSetBytes = $memory.WorkingSetPrivate
                    handles = $memory.HandleCount
                    threads = $memory.ThreadCount
                    gpu = @(Get-GpuRaw $script:rifflo)
                })
                $nextResource = $now + 2 * $script:clockFrequency
            }

            $sample = Get-Diagnostics
            if ($null -ne $sample -and $sample.stopwatchTimestamp -ne $prevSnapshot) {
                if ($prevSnapshot -ne 0) {
                    $longestSnapshotGap = [math]::Max($longestSnapshotGap, ($sample.stopwatchTimestamp - $prevSnapshot) / $script:clockFrequency)
                }
                $prevSnapshot = $sample.stopwatchTimestamp
                $diagWriter.WriteLine((ConvertTo-Json -InputObject $sample -Depth 12 -Compress))
                $diagnostics.Add($sample)
                foreach ($frame in $sample.spectrumFrames) { $spectrumFrames[[long]$frame.timestamp] = $frame }
                foreach ($frame in $sample.captionFrames) { $captionFrames[[long]$frame.timestamp] = $frame }
            }
            if ($prevSnapshot -and $now - $prevSnapshot -gt 5 * $script:clockFrequency) {
                throw 'The diagnostic snapshot stopped advancing during active sampling.'
            }
            if ($end - $start -ge $Seconds * $script:clockFrequency -and $prevSnapshot -ge $end) { break }
            Start-Sleep -Milliseconds 220
        } while ($true)
    } finally {
        $diagWriter.Dispose()
    }

    $span = ($end - $start) / $script:clockFrequency
    $spectrum = @($spectrumFrames.Values | Where-Object { $_.timestamp -ge $start -and $_.timestamp -lt $end })
    $caption = @($captionFrames.Values | Where-Object { $_.timestamp -ge $start -and $_.timestamp -lt $end })
    $fftSpan = ($diagnostics[-1].stopwatchTimestamp - $diagnostics[0].stopwatchTimestamp) / $script:clockFrequency
    $fftCount = $diagnostics[-1].fftCount - $diagnostics[0].fftCount
    $cpuSeconds = $resources[-1].cpuSeconds - $resources[0].cpuSeconds
    $memory = $resources.privateWorkingSetBytes | Measure-Object -Average -Minimum -Maximum
    $summary = [pscustomobject]@{
        scenario = $Name
        rate = $Rate
        pid = $script:rifflo.Id
        elapsedSeconds = $span
        startTimestamp = $start
        endTimestamp = $end
        snapshotCount = $diagnostics.Count
        maxSnapshotGapSeconds = $longestSnapshotGap
        cpuSeconds = $cpuSeconds
        oneLogicalCpuPercent = 100 * $cpuSeconds / $span
        privateWorkingSetMiB = [pscustomobject]@{ mean = $memory.Average / 1MB; min = $memory.Minimum / 1MB; max = $memory.Maximum / 1MB }
        gpu = Get-GpuPhase $resources.ToArray()
        fftCount = $fftCount
        fftHz = $fftCount / $fftSpan
        packetCount = $diagnostics[-1].packetCount - $diagnostics[0].packetCount
        captureAttempts = $diagnostics[-1].captureAttemptCount - $diagnostics[0].captureAttemptCount
        captureGenerations = @($diagnostics.captureGeneration | Sort-Object -Unique)
        renderTier = @($diagnostics.renderingTier | Sort-Object -Unique)
        nominalRates = @($diagnostics.animationFrameRate | Sort-Object -Unique)
        states = @($diagnostics.state | Sort-Object -Unique)
        modes = @($diagnostics.spectrumMode | Sort-Object -Unique)
        visibility = @($diagnostics.visible | Sort-Object -Unique)
        preview = @($diagnostics.preview | Sort-Object -Unique)
        clockRunning = @($diagnostics.animationClockRunning | Sort-Object -Unique)
        clockTicks = if ($diagnostics[0].PSObject.Properties['animationTickCount']) {
            $diagnostics[-1].animationTickCount - $diagnostics[0].animationTickCount
        } else { $null }
        captionRevisions = @($diagnostics.captionRevision | Sort-Object -Unique)
        overlayHandle = @($diagnostics.overlayWindowHandle | Sort-Object -Unique)
        windowGeometry = @($diagnostics | ForEach-Object { '{0},{1} {2}x{3}' -f $_.x, $_.y, $_.width, $_.height } | Sort-Object -Unique)
        visibleBarCounts = @($diagnostics | ForEach-Object { @($_.renderedBarHeights).Count } | Sort-Object -Unique)
        captionContentWidthDip = @($diagnostics | ForEach-Object { $_.overlayLayout.captionContentWidth } | Sort-Object -Unique)
        presetOriginTimestamp = if ($null -ne $diagnostics[0].presetAnimationSeconds) {
            $diagnostics[0].stopwatchTimestamp - [long]($diagnostics[0].presetAnimationSeconds * $script:clockFrequency)
        } else { $null }
        previewSpectrumChanges = $diagnostics[-1].previewSpectrumChangedCount - $diagnostics[0].previewSpectrumChangedCount
        previewCaptionChanges = $diagnostics[-1].previewCaptionOffsetCommitCount - $diagnostics[0].previewCaptionOffsetCommitCount
        spectrumChangesInSnapshotSpan = $diagnostics[-1].spectrumChangedCount - $diagnostics[0].spectrumChangedCount
        spectrum = Get-TraceStats $spectrum $span $Rate
        caption = Get-TraceStats $caption $span $Rate -Marquee
    }
    Save-Evidence "$phase.resources.json" $resources.ToArray()
    Save-Evidence "$phase.geometry.json" @{ spectrum = $spectrum; caption = $caption }
    Save-Evidence "$phase.summary.json" $summary
    $script:summaries.Add($summary)
    $textOnly = $Name -in 'marquee','bounce','loop'
    $animation = if ($textOnly) { $summary.caption } else { $summary.spectrum }
    $oneRate = if ($textOnly) { $animation.effectiveMovingHz } else { $animation.effectiveHz }
    Write-Host ('OBSERVED {0}: {1:N2} Hz p95 {2:N2}ms, {3:N3}% CPU, {4:N3} MiB private WS, {5:N3} FFT/s' -f $phase, $oneRate, $animation.p95Ms, $summary.oneLogicalCpuPercent, $summary.privateWorkingSetMiB.mean, $summary.fftHz)
    $summary
}

function Test-AnimationPhase([object]$Phase) {
    $name = $Phase.scenario
    $rate = $Phase.rate
    $minimum = if ($ProbeSeconds) { $ProbeSeconds } elseif ($name -in 'static', 'idle') { 5 } else { $PhaseSeconds }
    Add-Assertion "$name/$rate duration, preview closed and requested rate stable" (
        $Phase.elapsedSeconds -ge $minimum -and
        $Phase.maxSnapshotGapSeconds -lt 3.8 -and
        @($Phase.preview).Count -eq 1 -and !$Phase.preview[0] -and
        $Phase.nominalRates.Count -eq 1 -and $Phase.nominalRates[0] -eq $rate -and
        $Phase.previewSpectrumChanges -eq 0 -and $Phase.previewCaptionChanges -eq 0
    ) @{ duration = $Phase.elapsedSeconds; rates = $Phase.nominalRates; preview = $Phase.preview }
    if ($name -in @('live','preset','marquee')) {
        $other = if ($name -eq 'marquee') { $Phase.spectrum } else { $Phase.caption }
        $trace = if ($name -eq 'marquee') { $Phase.caption } else { $Phase.spectrum }
        $hz = if ($name -eq 'marquee') { $trace.effectiveMovingHz } else { $trace.effectiveHz }
        $rateOk = if ($rate -eq 30) { $hz -ge 28 -and $hz -le 31 } else { $hz -ge 55 -and $hz -le 61 -and $trace.p95Ms -le 34 }
        Add-Assertion "$name/$rate active trace meets rate and interval goals" (
            $rateOk -and $other.count -eq 0 -and $Phase.states.Count -eq 1 -and $Phase.states[0] -eq 'Playing' -and
            $Phase.visibility.Count -eq 1 -and $Phase.visibility[0]
        ) @{
            effectiveHz = $hz; p95Ms = $trace.p95Ms; otherTraceCount = $other.count
        }
        if ($name -eq 'live') {
            Add-Assertion "$name/$rate continuous real capture and FFT" (
                $Phase.fftHz -gt 0 -and $Phase.packetCount -gt 0 -and $Phase.captureAttempts -eq 0 -and
                $Phase.captureGenerations.Count -eq 1 -and $Phase.modes.Count -eq 1 -and $Phase.modes[0] -eq 'Live'
            ) @{ fftHz = $Phase.fftHz; modes = $Phase.modes; generations = $Phase.captureGenerations }
        }
    } else {
        Add-Assertion "$name/$rate no recurring visual work" (
            $Phase.spectrum.count -eq 0 -and $Phase.caption.count -eq 0 -and
            ($null -eq $Phase.clockTicks -or $Phase.clockTicks -eq 0) -and
            $Phase.clockRunning.Count -eq 1 -and !$Phase.clockRunning[0] -and
            $Phase.visibility.Count -eq 1 -and $Phase.visibility[0] -eq ($name -eq 'static') -and
            $Phase.states.Count -eq 1 -and ($name -eq 'idle' -or $Phase.states[0] -eq 'Playing')
        ) @{ clock = $Phase.clockRunning; overlayVisible = $Phase.visibility; playing = $Phase.states; fftHz = $Phase.fftHz }
    }
}

function Compare-AnimationPhases([string]$Name) {
    $low = $script:summaries | Where-Object { $_.scenario -eq $Name -and $_.rate -eq 30 } | Select-Object -Last 1
    $high = $script:summaries | Where-Object { $_.scenario -eq $Name -and $_.rate -eq 60 } | Select-Object -Last 1
    if ($null -eq $low -or $null -eq $high) { return }
    $cpuPp = $high.oneLogicalCpuPercent - $low.oneLogicalCpuPercent
    $memoryMiB = $high.privateWorkingSetMiB.mean - $low.privateWorkingSetMiB.mean
    $gpu = $null
    if ($low.gpu.quality -eq 'Continuous exact PID engine instances' -and
        $high.gpu.quality -eq 'Continuous exact PID engine instances') {
        $main = @($low.gpu.engines) + @($high.gpu.engines) | Sort-Object meanPercent -Descending | Select-Object -First 1
        $engine30 = $low.gpu.engines | Where-Object engine -EQ $main.engine | Select-Object -First 1
        $engine60 = $high.gpu.engines | Where-Object engine -EQ $main.engine | Select-Object -First 1
        if ($null -ne $engine30 -and $null -ne $engine60) {
            $gpu = [pscustomobject]@{
                engine = $main.engine; low = $engine30.meanPercent; high = $engine60.meanPercent
                deltaPercentagePoints = $engine60.meanPercent - $engine30.meanPercent
            }
        }
    }
    if ($Name -in 'live','preset','marquee') {
        Add-Assertion "$Name CPU and private WS within incremental budgets" ($cpuPp -le 5 -and $memoryMiB -le 10) @{
            cpuDeltaPercentagePoints = $cpuPp; privateWorkingSetDeltaMiB = $memoryMiB
        }
        if ($null -ne $gpu) {
            Add-Assertion "$Name main GPU engine within incremental budget" ($gpu.deltaPercentagePoints -le 3) $gpu
        }
    }
    if ($Name -eq 'marquee' -and !$ProbeSeconds) {
        $lowDistance = $low.caption.distanceAt30sDip
        $highDistance = $high.caption.distanceAt30sDip
        Add-Assertion 'marquee has the same travel over a 30 second interval' (
            $null -ne $lowDistance -and $null -ne $highDistance -and [math]::Abs($highDistance - $lowDistance) -le 1 -and
            [math]::Abs($lowDistance - 900) -le 1
        ) @{ lowDistanceDip = $lowDistance; highDistanceDip = $highDistance }
    }
    if ($Name -eq 'live') {
        Add-Assertion 'live FFT frequency stays within 5 percent across rates' ([math]::Abs($high.fftHz - $low.fftHz) -le $low.fftHz * 0.05) @{
            low = $low.fftHz; high = $high.fftHz
        }
    }
    [pscustomobject]@{ scenario = $Name; cpuDeltaPercentagePoints = $cpuPp; privateWorkingSetDeltaMiB = $memoryMiB; gpu = $gpu }
}

function Test-TrayMarks([int]$Rate, [string]$Step) {
    $marks = Set-TrayAnimationRate $Rate $true "$Step.png"
    Add-Assertion "$Step exactly one real tray choice is checked" (
        $marks.low -eq ($Rate -eq 30) -and $marks.high -eq ($Rate -eq 60)
    ) $marks
    $marks
}

function Test-PauseDuration([int]$Rate) {
    Set-TrayAnimationRate $Rate | Out-Null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Set-Playback $script:player $script:shortTitle 'dynamic.wav' 'paused' -Artist ''
    $seenVisible = $false
    do {
        $window = Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗'
        if ($null -eq $window) { break }
        $seenVisible = $true
        Start-Sleep -Milliseconds 40
    } while ($timer.Elapsed.TotalSeconds -lt 4)
    $pauseMs = $timer.Elapsed.TotalMilliseconds
    $after = Wait-Diagnostics 'paused and hidden after the tail' { param($s) !$s.visible -and $s.state -eq 'Paused' }
    Start-AnimationPlayback $script:shortTitle
    Wait-Diagnostics 'visible again after resuming' { param($s) $s.visible -and $s.state -eq 'Playing' } | Out-Null
    [pscustomobject]@{ rate = $Rate; commandToHideMs = $pauseMs; overlaySeenDuringTail = $seenVisible; diagnosticAfter = $after }
}

function Test-SteadyTone {
    Set-AnimationScenario 'live'
    Set-Playback $script:player 'Rifflo steady 200 Hz' 'steady.wav' -Artist ''
    Wait-Diagnostics 'steady narrow-band PCM captured' {
        param($s) $s.title -eq 'Rifflo steady 200 Hz' -and $s.spectrumMode -eq 'Live' -and $s.captureRunning
    } | Out-Null
    $tonePhases = @(foreach ($rate in @(30, 60)) {
        Set-TrayAnimationRate $rate | Out-Null
        Start-Sleep -Seconds 5
        $phase = Measure-AnimationPhase 'steady-tone' $rate 8
        $skipped = if ($null -ne $phase.clockTicks) { $phase.clockTicks - $phase.spectrumChangesInSnapshotSpan }
        Add-Assertion "steady-tone/$rate preserves live analysis and sparsely commits changed geometry" (
            $phase.fftHz -gt 0 -and $phase.spectrum.effectiveHz -lt $rate * 0.8 -and $phase.caption.count -eq 0
        ) @{
            fftHz = $phase.fftHz; schedulerTicks = $phase.clockTicks; sameSpanGeometryChanges = $phase.spectrumChangesInSnapshotSpan
            skippedSchedulerTicks = $skipped; changedGeometryHz = $phase.spectrum.effectiveHz
        }
        $phase
    })
    Save-Evidence '08-steady-tone.json' $tonePhases
}

function Test-MarqueeEndpoint([ValidateSet('bounce','loop')][string]$ModeName) {
    Set-AnimationScenario 'static'
    $settings = Open-AnimationSettings
    $label = if ($ModeName -eq 'bounce') { '左右往返' } else { '单向循环' }
    Select-SettingOption $settings 'ScrollMode' $label
    Close-AnimationSettings $settings
    $endpointTitle = 'Rifflo ' + ('W' * 15)
    $observed = @(foreach ($rate in @(30, 60)) {
        Set-TrayAnimationRate $rate | Out-Null
        Start-AnimationPlayback $script:shortTitle
        $commandTime = [Diagnostics.Stopwatch]::GetTimestamp()
        Start-AnimationPlayback $endpointTitle
        $initial = Get-Diagnostics
        $limit = $initial.overlayLayout.captionContentWidth - $initial.overlayLayout.caption.width
        if (!$initial.overlayLayout.captionScrolling -or $limit -lt 30) { throw 'The short marquee fixture does not move far enough.' }
        $travelCount = if ($ModeName -eq 'bounce') { 2 } else { 1 }
        $span = [int][math]::Ceiling(6 + $travelCount * $limit / 30)
        $phase = Measure-AnimationPhase $ModeName $rate $span
        $frames = (Read-JsonSnapshot (Join-Path $EvidenceDirectory "$ModeName-$rate.geometry.json")).caption
        $outbound = $frames | Where-Object value -GT 0 | Select-Object -First 1
        $peak = $frames | Where-Object value -GE ($limit - 0.001) | Select-Object -First 1
        if (!$outbound -or !$peak) { throw 'The marquee fixture did not traverse to the endpoint.' }
        $return = $frames | Where-Object { $_.timestamp -gt $peak.timestamp -and $_.value -lt ($limit - 0.001) } | Select-Object -First 1
        if (!$return) { throw 'The fixture stayed at the terminal position.' }
        $result = [pscustomobject]@{
            rate = $rate
            travelDip = $limit
            fixtureCommandToFirstMoveSeconds = ($outbound.timestamp - $commandTime) / $script:clockFrequency
            endpointHoldSeconds = ($return.timestamp - $peak.timestamp) / $script:clockFrequency
            firstReturnStepDip = $return.value - $peak.value
            departureOffsetDip = $return.value
            clocks = $phase.clockTicks
        }
        $expectedDeparture = if ($ModeName -eq 'bounce') {
            $result.firstReturnStepDip -lt 0 -and $result.firstReturnStepDip -gt -2
        } else {
            [math]::Abs($result.departureOffsetDip) -lt 0.1
        }
        Add-Assertion "$ModeName/$rate holds before moving and follows the configured return" (
            $result.fixtureCommandToFirstMoveSeconds -ge 1.75 -and $result.fixtureCommandToFirstMoveSeconds -le 2.6 -and
            $result.endpointHoldSeconds -ge 1.90 -and $result.endpointHoldSeconds -le 2.10 -and
            $expectedDeparture
        ) $result
        $result
    })
    $file = if ($ModeName -eq 'bounce') { '09-bounce-endpoints.json' } else { '10-loop-endpoints.json' }
    Save-Evidence $file $observed
}

function Test-DesktopAnimation {
    $initial = Get-Diagnostics
    Add-Assertion 'a new isolated settings path starts at 30 fps' ($initial.animationFrameRate -eq 30 -and !(Test-Path -LiteralPath $script:settingsPath)) $initial.animationFrameRate
    Test-TrayMarks 30 '01-fresh-30' | Out-Null
    Stop-AnimationApp

    $configTransitions = [Collections.Generic.List[object]]::new()
    foreach ($example in @(
        @{ name = 'field absent'; json = '{"barCount":19,"textWidth":311,"showArtist":false}' },
        @{ name = 'invalid number'; json = '{"barCount":19,"textWidth":311,"showArtist":false,"animationFrameRate":45}' },
        @{ name = 'invalid object'; json = '{"barCount":19,"textWidth":311,"showArtist":false,"animationFrameRate":{"fps":60}}' }
    )) {
        Set-Content -LiteralPath $script:settingsPath -Value $example.json -Encoding UTF8
        Start-AnimationApp
        $atLoad = Get-Diagnostics
        $settings = Open-AnimationSettings
        $width = (Get-SettingControl $settings 'TextWidth').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
        $artists = (Get-SettingControl $settings 'ShowArtist').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString()
        Set-TrayAnimationRate 60 | Out-Null
        $saved = Wait-Observation 'frame rate saved with settings preserved' { Read-JsonSnapshot $script:settingsPath } {
            param($s) $s.PSObject.Properties['animationFrameRate'] -and $s.animationFrameRate -eq 60
        }
        $after = Get-Diagnostics
        Close-AnimationSettings $settings
        Add-Assertion "configuration with $($example.name) falls back and retains other fields" (
            $atLoad.animationFrameRate -eq 30 -and $width -eq 311 -and $artists -eq 'Off' -and
            $saved.barCount -eq 19 -and $saved.textWidth -eq 311 -and !$saved.showArtist -and $after.animationFrameRate -eq 60
        ) @{ sourceJson = $example.json; rateAfterLoad = $atLoad.animationFrameRate; afterSave = $saved }
        $configTransitions.Add([pscustomobject]@{ start = $atLoad; after = $after; saved = $saved })
        Stop-AnimationApp
    }
    Save-Evidence '02-config-transitions.json' $configTransitions.ToArray()

    $oldPid = $script:rifflo.Id
    Start-AnimationApp
    $restored = Get-Diagnostics
    Add-Assertion 'tray exit and restart restore 60 fps in a new process' ($restored.animationFrameRate -eq 60 -and $script:rifflo.Id -ne $oldPid) @{
        previousPid = $oldPid; currentPid = $script:rifflo.Id; restoredRate = $restored.animationFrameRate
    }
    Test-TrayMarks 60 '03-restart-60' | Out-Null
    $settings = Open-AnimationSettings
    Wait-Diagnostics 'actively drawn idle preview' { param($s) $s.preview -and $s.previewSpectrumChangedCount -gt 0 } | Out-Null
    $beforePreview = Get-Diagnostics
    Set-TrayAnimationRate 30 | Out-Null
    $afterPreview30 = Wait-Diagnostics 'preview geometry keeps updating at 30 fps' {
        param($s) $s.preview -and $s.animationFrameRate -eq 30 -and $s.previewSpectrumChangedCount -gt $beforePreview.previewSpectrumChangedCount
    }
    $checkbox = Get-SettingControl $settings 'Animation60Fps'
    $toggle = $checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $checkbox30 = $toggle.Current.ToggleState.ToString()
    Start-Sleep -Seconds 2
    $preview30Trace = Get-Diagnostics
    $preview30Span = ($preview30Trace.stopwatchTimestamp - $afterPreview30.stopwatchTimestamp) / $script:clockFrequency
    $preview30Frames = @($preview30Trace.previewSpectrumFrames | Where-Object timestamp -GT $afterPreview30.stopwatchTimestamp)
    $previewStats30 = Get-TraceStats $preview30Frames $preview30Span 30
    Set-TrayAnimationRate 60 | Out-Null
    $afterPreview60 = Wait-Diagnostics 'preview geometry keeps updating at 60 fps' {
        param($s) $s.preview -and $s.animationFrameRate -eq 60 -and $s.previewSpectrumChangedCount -gt $afterPreview30.previewSpectrumChangedCount
    }
    $checkbox60 = $toggle.Current.ToggleState.ToString()
    Start-Sleep -Seconds 2
    $preview60Trace = Get-Diagnostics
    $previewSpanTicks = $preview60Trace.stopwatchTimestamp - $afterPreview60.stopwatchTimestamp
    $previewFrames = @($preview60Trace.previewSpectrumFrames | Where-Object timestamp -GT $afterPreview60.stopwatchTimestamp)
    $previewStats60 = Get-TraceStats $previewFrames ($previewSpanTicks / $script:clockFrequency) 60
    Add-Assertion 'tray selections and the open preview and settings toggle stay synchronized' (
        $checkbox30 -eq 'Off' -and $checkbox60 -eq 'On' -and
        $beforePreview.previewWindowHandle -eq $afterPreview60.previewWindowHandle -and
        $previewStats30.effectiveHz -ge 28 -and $previewStats30.effectiveHz -le 31 -and
        $previewStats60.effectiveHz -ge 55 -and $previewStats60.effectiveHz -le 61
    ) @{ checkbox30 = $checkbox30; checkbox60 = $checkbox60; stats30 = $previewStats30; stats60 = $previewStats60 }

    Invoke-Uia (Get-SettingControl $settings 'ResetDefaults')
    $reset = Wait-Diagnostics 'reset defaults applied to preview' { param($s) $s.animationFrameRate -eq 30 -and $s.preview }
    $savedDefault = Wait-Observation 'reset defaults persisted' { Read-JsonSnapshot $script:settingsPath } {
        param($s) $s.animationFrameRate -eq 30 -and $s.barCount -eq 16 -and $s.textWidth -eq 260
    }
    Start-Sleep -Seconds 2
    $resetTrace = Get-Diagnostics
    $resetSpan = ($resetTrace.stopwatchTimestamp - $reset.stopwatchTimestamp) / $script:clockFrequency
    $resetFrames = @($resetTrace.previewSpectrumFrames | Where-Object timestamp -GT $reset.stopwatchTimestamp)
    $resetStats = Get-TraceStats $resetFrames $resetSpan 30
    Add-Assertion 'restore defaults resets actual rate and preview with settings toggled off' (
        $reset.animationFrameRate -eq 30 -and $toggle.Current.ToggleState.ToString() -eq 'Off' -and
        $resetStats.effectiveHz -ge 28 -and $resetStats.effectiveHz -le 31
    ) @{ configuration = $savedDefault; preview = $resetStats }
    Test-TrayMarks 30 '04-reset-30' | Out-Null
    Close-AnimationSettings $settings
    $closed1 = Get-Diagnostics
    Start-Sleep -Seconds 2
    $closed2 = Get-Diagnostics
    Add-Assertion 'closed preview and no-session stop both traces and the clock' (
        !$closed2.animationClockRunning -and $closed1.previewSpectrumChangedCount -eq $closed2.previewSpectrumChangedCount -and
        $closed1.previewCaptionOffsetCommitCount -eq $closed2.previewCaptionOffsetCommitCount
    ) @{ before = $closed1.previewSpectrumChangedCount; after = $closed2.previewSpectrumChangedCount }
    Save-Evidence '04-preview-reset.json' @{
        start = $beforePreview; fps30 = $afterPreview30; fps60 = $afterPreview60
        preview30Trace = $preview30Trace; preview60Trace = $preview60Trace; reset = $reset; resetTrace = $resetTrace
        closed = @($closed1, $closed2)
    }

    Set-AnimationScenario 'live'
    $live = Get-Diagnostics
    $script:monitorEvidence = Get-OverlayMonitor (Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗')
    Save-Evidence 'display.json' $script:monitorEvidence
    $continuity = [Collections.Generic.List[object]]::new()
    foreach ($scene in @('live','preset','marquee')) {
        if ($scene -ne 'live') { Set-AnimationScenario $scene }
        if ($scene -eq 'marquee') {
            Wait-Diagnostics 'moving long title' { param($s) $s.captionOffsetCommitCount -gt 20 } | Out-Null
        }
        $before = Get-Diagnostics
        Set-TrayAnimationRate 60 | Out-Null
        $at60 = Get-Diagnostics
        Set-TrayAnimationRate 30 | Out-Null
        $back30 = Get-Diagnostics
        $snapshots = @($before, $at60, $back30)
        $unchanged = @($snapshots | Where-Object {
            $_.overlayWindowHandle -ne $before.overlayWindowHandle -or $_.captionRevision -ne $before.captionRevision -or
            $_.title -ne $before.title -or $_.source -ne $before.source -or !$_.visible
        }).Count -eq 0
        $detail = [ordered]@{ scene = $scene; sameTrackAndWindow = $unchanged; snapshots = $snapshots }
        $continuous = switch ($scene) {
            'live' {
                @($snapshots | Where-Object {
                    $_.captureAttemptCount -ne $before.captureAttemptCount -or $_.captureGeneration -ne $before.captureGeneration -or !$_.captureRunning
                }).Count -eq 0 -and $back30.fftCount -gt $before.fftCount -and $back30.spectrumChangedCount -gt $before.spectrumChangedCount
            }
            'preset' {
                $dt = ($back30.stopwatchTimestamp - $before.stopwatchTimestamp) / $script:clockFrequency
                $step = $back30.presetAnimationSeconds - $before.presetAnimationSeconds
                $detail['phaseDifferenceSeconds'] = $step
                $detail['clockDifferenceSeconds'] = $dt
                $at60.presetAnimationSeconds -gt $before.presetAnimationSeconds -and [math]::Abs($step - $dt) -lt 0.10
            }
            'marquee' {
                $trace = @($snapshots.captionFrames | Sort-Object timestamp -Unique | Where-Object timestamp -GE $before.captionFrames[-1].timestamp)
                $maxResidual = 0.0
                foreach ($commit in $trace) {
                    $expected = $trace[0].value + 30 * ($commit.timestamp - $trace[0].timestamp) / $script:clockFrequency
                    $maxResidual = [math]::Max($maxResidual, [math]::Abs($commit.value - $expected))
                }
                $detail['maxConstantMotionResidualDip'] = $maxResidual
                $detail['traceCount'] = $trace.Count
                $trace.Count -gt 40 -and $trace[-1].value -gt $trace[0].value -and $maxResidual -lt 2
            }
        }
        $detail['continuous'] = $continuous
        $continuity.Add([pscustomobject]$detail)
        Add-Assertion "$scene 30-60-30 keeps same window, media and animation trajectory" ($unchanged -and $continuous) @{
            sameTrackAndWindow = $unchanged; continuous = $continuous
        }
    }
    Save-Evidence '05-switch-continuity.json' $continuity.ToArray()

    Set-AnimationScenario 'preset'
    $pause30 = Test-PauseDuration 30
    $pause60 = Test-PauseDuration 60
    Save-Evidence '06-pause.json' @($pause30, $pause60)
    Add-Assertion 'preset pause hides after about two seconds at both frame rates' (
        $pause30.overlaySeenDuringTail -and $pause60.overlaySeenDuringTail -and
        $pause30.commandToHideMs -ge 1650 -and $pause30.commandToHideMs -le 2700 -and
        $pause60.commandToHideMs -ge 1650 -and $pause60.commandToHideMs -le 2700 -and
        [math]::Abs($pause60.commandToHideMs - $pause30.commandToHideMs) -le 350
    ) @{ ms30 = $pause30.commandToHideMs; ms60 = $pause60.commandToHideMs }
    Test-SteadyTone
    Test-MarqueeEndpoint 'bounce'
    Test-MarqueeEndpoint 'loop'

    Set-AnimationScenario 'hidden'
    $hiddenStart = Get-Diagnostics
    $cycleStartResources = Get-ProcessSample $script:rifflo
    $cycles = @(foreach ($cycle in 1..6) {
        Set-TrayAnimationRate 30 | Out-Null
        Start-Sleep -Milliseconds 500
        Set-TrayAnimationRate 60 | Out-Null
        $beforeSettings = Get-Diagnostics
        Set-Playback $script:player $script:shortTitle 'dynamic.wav' 'paused' -Artist ''
        Wait-Diagnostics 'paused fixture allows appearance preview' { param($s) $s.state -eq 'Paused' } | Out-Null
        $settings = Open-AnimationSettings
        Wait-Diagnostics 'manually hidden preview resumes independently' {
            param($s) $s.manuallyHidden -and $s.preview -and $s.previewSpectrumChangedCount -gt $beforeSettings.previewSpectrumChangedCount
        } | Out-Null
        Close-AnimationSettings $settings
        Start-AnimationPlayback $script:shortTitle
        Invoke-RiffloTray '恢复悬浮窗'
        $visible = Wait-Diagnostics 'restored hidden-selected 60 fps overlay resumes' {
            param($s) !$s.manuallyHidden -and $s.visible -and $s.animationFrameRate -eq 60 -and $s.spectrumChangedCount -gt $beforeSettings.spectrumChangedCount
        }
        Invoke-RiffloTray '隐藏悬浮窗'
        $hidden = Wait-Diagnostics 'hidden again after independent preview and overlay cycle' {
            param($s) $s.manuallyHidden -and !$s.preview -and !$s.animationClockRunning
        }
        [pscustomobject]@{ cycle = $cycle; shown = $visible; hidden = $hidden; resources = Get-ProcessSample $script:rifflo }
    })
    $settled = @(foreach ($sample in 1..3) {
        Start-Sleep -Seconds 2
        Get-ProcessSample $script:rifflo
    })
    Save-Evidence '07-hide-preview-cycles.json' @{ start = $cycleStartResources; cycle = $cycles; settled = $settled }
    Add-Assertion 'six hidden rate and preview cycles retain media and settle to zero visual work' (
        @($cycles | Where-Object { $_.shown.title -ne $hiddenStart.title -or $_.hidden.animationClockRunning -or $_.hidden.preview -or $_.hidden.visible }).Count -eq 0
    ) @{
        cycles = $cycles.Count
        privateWorkingSetMiB = @($cycles | ForEach-Object { $_.resources.privateWorkingSetBytes / 1MB })
        settledPrivateWorkingSetMiB = @($settled | ForEach-Object { $_.privateWorkingSetBytes / 1MB })
        settledHandleCounts = $settled.handleCount
    }
    Invoke-RiffloTray '恢复悬浮窗'
    Wait-Diagnostics 'overlay unhidden before ending functional pass' { param($s) !$s.manuallyHidden } | Out-Null
    Stop-AnimationPlayback
}

try {
    if (!(Test-Path -LiteralPath $script:exe)) { throw "The published Rifflo executable was not found at $script:exe." }
    $fixtureProject = Join-Path $repo 'tests\Rifflo.PlaybackFixture\Rifflo.PlaybackFixture.csproj'
    $script:fixture = Join-Path (Split-Path $fixtureProject) 'bin\Release\net10.0-windows10.0.26100.0\Rifflo.PlaybackFixture.exe'
    $dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet).Source }
    & $dotnet build $fixtureProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Building the Windows playback fixture failed.' }
    & $script:fixture --generate-animation $script:tempDir
    if ($LASTEXITCODE -ne 0) { throw 'Generating the varying playback fixture failed.' }
    Initialize-DesktopInspection
    $session = (Get-Process -Id $PID).SessionId
    $shell = [RiffloVerificationNative]::Windows(0) | Where-Object ClassName -EQ 'Shell_TrayWnd' | Select-Object -First 1
    if ($null -eq $shell -or (Get-Process -Id $shell.ProcessId).SessionId -ne $session -or $session -eq 0) {
        throw 'The benchmark requires the interactive desktop and Explorer tray.'
    }
    $releaseDll = Join-Path (Split-Path $script:exe) 'Rifflo.dll'
    $renderEndpoints = (& $script:fixture --render-endpoints | ConvertFrom-Json)
    $defaultAudioOutput = $renderEndpoints.devices | Where-Object id -EQ $renderEndpoints.defaultDeviceId | Select-Object -ExpandProperty name -First 1
    $userConfig = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Rifflo\settings.json'
    $userConfigBefore = if (Test-Path -LiteralPath $userConfig) { (Get-FileHash -LiteralPath $userConfig).Hash }
    Save-Evidence 'environment.json' @{
        capturedAt = [DateTimeOffset]::UtcNow.ToString('O')
        os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture
        cpu = Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors
        gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion)
        monitors = @([RiffloVerificationNative]::Monitors())
        sessionId = $session
        powershellVersion = $PSVersionTable.PSVersion.ToString()
        releaseExe = $script:exe
        releaseExeSha256 = (Get-FileHash -LiteralPath $script:exe).Hash
        releaseDllSha256 = (Get-FileHash -LiteralPath $releaseDll).Hash
        releaseVersion = (Get-Item -LiteralPath $script:exe).VersionInfo.ProductVersion
        fixtureWaveSha256 = (Get-FileHash -LiteralPath (Join-Path $script:tempDir 'dynamic.wav')).Hash
        defaultAudioOutput = $defaultAudioOutput
        frameTraceEnabled = $true
        qpcTicksPerSecond = $script:clockFrequency
        runConfiguration = 'Release, Windows x64 published runtime'
        reverseOrder = $ReverseOrder.IsPresent
        requestedRateOrder = $RateOrder
        probeSeconds = $ProbeSeconds
    }
    Start-AnimationApp
    if ($Preflight) {
        Start-AnimationPlayback $script:shortTitle
        $live = Wait-Diagnostics 'old build captures varying playback fixture' { param($s) $s.visible -and $s.spectrumMode -eq 'Live' -and $s.fftCount -gt 0 }
        $script:monitorEvidence = Get-OverlayMonitor (Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗')
        Save-Evidence 'display.json' $script:monitorEvidence
        $samples = @(foreach ($step in 1..5) {
            $sample = Get-ProcessSample $script:rifflo
            Add-Member -InputObject $sample -NotePropertyName gpu -NotePropertyValue @(Get-GpuRaw $script:rifflo)
            $sample
            Start-Sleep -Milliseconds 950
        })
        Save-Evidence 'preflight.json' $samples
        Invoke-RiffloTray '隐藏悬浮窗'
        Wait-Diagnostics 'old published tray can hide isolated overlay' { param($s) $s.manuallyHidden -and !$s.visible } | Out-Null
        Write-Host 'PASS interactive fixture, native overlay monitor, old published tray, process CPU/memory counters and GPU query'
    } else {
        $initial = Get-Diagnostics
        if (!$initial.PSObject.Properties['stopwatchTimestamp'] -or !$initial.PSObject.Properties['spectrumFrames']) {
            throw 'The published build does not provide the geometry tracing contract.'
        }
        if ($initial.stopwatchFrequency -ne $script:clockFrequency) { throw 'The local and published monotonic clocks use different frequencies.' }
        if ($DesktopPixelsOnly) {
            if ($DesktopCaptureMethod -ne 'GDI') {
                Add-Type -Path (Join-Path $PSScriptRoot 'Rifflo.Verification.DesktopDuplication.cs')
            }
            $pixelRates = if ($RateOrder) { $RateOrder } else { @(30,60) }
            $pixelResults = @(foreach ($scene in $Scenarios | Where-Object { $_ -in 'static','live','preset','marquee' }) {
                Set-AnimationScenario $scene
                foreach ($rate in $pixelRates) {
                    Set-TrayAnimationRate $rate | Out-Null
                    Measure-DesktopPixels $scene $rate
                }
            })
            Save-Evidence 'composed-desktop.json' $pixelResults
        } else {
            if (!$SkipDesktopChecks) { Test-DesktopAnimation }
        }
        if (!$DesktopChecksOnly -and !$DesktopPixelsOnly) {
            $rates = if ($RateOrder) { $RateOrder } elseif ($ReverseOrder) { @(60,30) } else { @(30,60) }
            foreach ($scene in $Scenarios) {
                Set-AnimationScenario $scene
                foreach ($rate in $rates) {
                    Set-TrayAnimationRate $rate | Out-Null
                    if ($scene -eq 'marquee') {
                        # Changing the actual fixture title restarts the marquee before the warm-up.
                        Start-AnimationPlayback $script:shortTitle
                        Start-AnimationPlayback $script:longTitle
                    }
                    if ($null -eq $script:monitorEvidence -and $scene -in 'live','preset','marquee','static') {
                        $script:monitorEvidence = Get-OverlayMonitor (Find-RiffloWindow $script:rifflo 'Rifflo 桌面悬浮窗')
                        Save-Evidence 'display.json' $script:monitorEvidence
                    }
                    Start-Sleep -Seconds 5
                    $duration = if ($ProbeSeconds) { $ProbeSeconds } elseif ($scene -in 'static','idle') { 8 } else { $PhaseSeconds }
                    Test-AnimationPhase (Measure-AnimationPhase $scene $rate $duration)
                }
            }
            Save-Evidence 'comparisons.json' @($Scenarios | ForEach-Object { Compare-AnimationPhases $_ })
        }
    }
    Stop-AnimationApp
    $userConfigAfter = if (Test-Path -LiteralPath $userConfig) { (Get-FileHash -LiteralPath $userConfig).Hash }
    Add-Assertion 'default user settings file SHA-256 retained' ($userConfigBefore -eq $userConfigAfter) @{
        path = $userConfig; before = $userConfigBefore; after = $userConfigAfter
    }
} catch {
    Save-Evidence 'error.json' @{ message = $_.Exception.Message; stack = $_.ScriptStackTrace }
    throw
} finally {
    Save-Evidence 'assertions.json' $script:assertions.ToArray()
    Save-Evidence 'summaries.json' $script:summaries.ToArray()
    foreach ($process in $script:ownedProcesses) { Stop-TestProcess $process }
    $resolved = (Resolve-Path -LiteralPath $script:tempDir).Path
    $temp = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
    if ((Split-Path -Parent $resolved).TrimEnd('\') -eq $temp.TrimEnd('\') -and (Split-Path -Leaf $resolved) -like 'RiffloAnimation-*') {
        Remove-Item -LiteralPath $resolved -Recurse -ErrorAction SilentlyContinue
    }
}
if (@($script:assertions | Where-Object passed -EQ $false).Count -gt 0) { exit 2 }

function Read-JsonSnapshot([string]$Path) {
    if (![IO.File]::Exists($Path)) { return $null }
    try { [IO.File]::ReadAllText($Path) | ConvertFrom-Json }
    catch [IO.IOException] { return $null }
    catch [System.ArgumentException] { return $null }
}

function Wait-Observation([string]$Description, [scriptblock]$Observe, [scriptblock]$Condition, [int]$Seconds = 12) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $last = & $Observe
        if ($null -ne $last -and (& $Condition $last)) { return $last }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt $Seconds)
    throw "$Description did not reach the expected state. Last observation: $($last | ConvertTo-Json -Depth 8 -Compress)"
}

function Save-Evidence([string]$Name, $Value) {
    $path = Join-Path $EvidenceDirectory $Name
    ConvertTo-Json -InputObject $Value -Depth 12 | Set-Content -LiteralPath $path -Encoding UTF8
}

function Add-Assertion([string]$Name, [bool]$Passed, $Detail) {
    $script:assertions.Add([pscustomobject]@{ check = $Name; passed = $Passed; detail = $Detail })
    Write-Host ('{0} {1}' -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name)
}

function Register-TestProcess([Diagnostics.Process]$Process) {
    $script:ownedProcesses.Add($Process)
    return $Process
}

function Stop-TestProcess([Diagnostics.Process]$Process) {
    if ($null -eq $Process -or $Process.HasExited) { return }
    $Process.CloseMainWindow() | Out-Null
    if (!$Process.WaitForExit(2000)) { $Process.Kill($true); $Process.WaitForExit(3000) | Out-Null }
}

function Start-Rifflo {
    $previousSettings = $env:RIFFLO_SETTINGS_PATH
    $previousDiagnostics = $env:RIFFLO_DIAGNOSTICS
    $previousMediaPrefix = $env:RIFFLO_TEST_MEDIA_PREFIX
    try {
        $env:RIFFLO_SETTINGS_PATH = $script:settingsPath
        $env:RIFFLO_DIAGNOSTICS = $script:diagPath
        $env:RIFFLO_TEST_MEDIA_PREFIX = if ($Mode -eq 'Edge') { $null } else { 'Rifflo.Verification.' }
        Register-TestProcess (Start-Process -FilePath $script:exe -WorkingDirectory (Split-Path $script:exe) -PassThru)
    } finally {
        $env:RIFFLO_SETTINGS_PATH = $previousSettings
        $env:RIFFLO_DIAGNOSTICS = $previousDiagnostics
        $env:RIFFLO_TEST_MEDIA_PREFIX = $previousMediaPrefix
    }
}

function Get-Diagnostics {
    $sample = Read-JsonSnapshot $script:diagPath
    if ($null -ne $sample -and $sample.title -and $sample.source -notlike 'Rifflo.Verification.*' -and $sample.title -notin 'Rifflo Edge 200 Hz', 'Rifflo Edge 6000 Hz') {
        $sample.title = '[existing desktop media session]'
        $sample.artist = ''
    }
    return $sample
}

function Wait-Diagnostics([string]$Description, [scriptblock]$Condition, [int]$Seconds = 15) {
    Wait-Observation $Description { Get-Diagnostics } $Condition $Seconds
}

function Start-PlaybackFixture([string]$Slot) {
    $command = Join-Path $script:tempDir "$Slot.json"
    $status = Join-Path $script:tempDir "$Slot.status.json"
    Remove-Item -LiteralPath $status -ErrorAction SilentlyContinue
    $aumid = "Rifflo.Verification.$Slot"
    $process = Register-TestProcess (Start-Process -FilePath $script:fixture -ArgumentList @("`"$command`"", $aumid, "RiffloFixture$Slot") -WindowStyle Hidden -PassThru)
    [pscustomobject]@{ process = $process; aumid = $aumid; command = $command; status = $status }
}

function Set-Playback([object]$Player, [string]$Title, [string]$File, [string]$State = 'playing', [switch]$NoWait,
    [string]$Artist, [string]$AlbumTitle, [uint32]$TrackNumber, [switch]$AudioDisconnected) {
    $token = [guid]::NewGuid().ToString()
    $request = @{ title = $Title; file = (Join-Path $script:tempDir $File); state = $State; token = $token }
    if ($PSBoundParameters.ContainsKey('Artist')) { $request.artist = $Artist }
    if ($PSBoundParameters.ContainsKey('AlbumTitle')) { $request.albumTitle = $AlbumTitle }
    if ($PSBoundParameters.ContainsKey('TrackNumber')) { $request.trackNumber = $TrackNumber }
    if ($AudioDisconnected) { $request.audioDisconnected = $true }
    $scratch = $Player.command + '.tmp'
    ConvertTo-Json -InputObject $request -Compress | Set-Content -LiteralPath $scratch -Encoding UTF8
    [IO.File]::Move($scratch, $Player.command, $true)
    if (!$NoWait) {
        $ack = Wait-Observation "$Title fixture transport" { Read-JsonSnapshot $Player.status } { param($s) $s.token -eq $token }
        if ($ack.error) { throw "Playback fixture rejected ${Title}: $($ack.error)" }
    }
}

function Get-PlaybackSessions {
    $result = (& $script:fixture --sessions | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate GSMTC sessions.' }
    foreach ($session in $result.sessions) {
        if ($session.sourceAppUserModelId -notlike 'Rifflo.Verification.*' -and $session.title -notin 'Rifflo Edge 200 Hz', 'Rifflo Edge 6000 Hz') {
            $session.title = '[existing desktop media session]'
            $session.artist = ''
        }
    }
    return $result
}

function Get-AudioSessions {
    $result = (& $script:fixture --audio-sessions | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the default multimedia endpoint audio sessions.' }
    return $result
}

function Get-ProcessSample([Diagnostics.Process]$Process) {
    $Process.Refresh()
    if ($Process.HasExited) { throw "Rifflo PID $($Process.Id) exited while collecting samples." }
    $memory = Get-CimInstance -ClassName Win32_PerfRawData_PerfProc_Process -Filter "IDProcess = $($Process.Id)"
    if ($null -eq $memory) { throw "No performance counters for PID $($Process.Id)." }
    [pscustomobject]@{
        utc = [DateTimeOffset]::UtcNow.ToString('O')
        processId = $Process.Id
        cpuSeconds = $Process.TotalProcessorTime.TotalSeconds
        privateWorkingSetBytes = $memory.WorkingSetPrivate
        privateBytes = $memory.PrivateBytes
        threadCount = $memory.ThreadCount
        handleCount = $memory.HandleCount
        diagnostics = Get-Diagnostics
    }
}

function Measure-RiffloPhase([Diagnostics.Process]$Process, [string]$Phase, [int]$Seconds) {
    $raw = [System.Collections.Generic.List[object]]::new()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $sample = Get-ProcessSample $Process
        Add-Member -InputObject $sample -NotePropertyName 'elapsedSeconds' -NotePropertyValue $timer.Elapsed.TotalSeconds
        $raw.Add($sample)
        if ($timer.Elapsed.TotalSeconds -ge $Seconds) { break }
        Start-Sleep -Seconds 2
    } while ($true)
    $first = $raw[0]
    $last = $raw[$raw.Count - 1]
    $elapsed = $last.elapsedSeconds - $first.elapsedSeconds
    $cores = [Environment]::ProcessorCount
    $cpu = $last.cpuSeconds - $first.cpuSeconds
    $workingSet = $raw.privateWorkingSetBytes | Measure-Object -Average -Minimum -Maximum
    $summary = [pscustomobject]@{
        phase = $Phase
        elapsedSeconds = [math]::Round($elapsed, 3)
        sampleCount = $raw.Count
        cpuSeconds = [math]::Round($cpu, 3)
        logicalProcessorCount = $cores
        cpuPercentAllLogicalProcessors = [math]::Round(100 * $cpu / ($cores * $elapsed), 4)
        cpuPercentOfOneLogicalProcessor = [math]::Round(100 * $cpu / $elapsed, 4)
        privateWorkingSetMiB = [pscustomobject]@{
            min = [math]::Round($workingSet.Minimum / 1MB, 3)
            mean = [math]::Round($workingSet.Average / 1MB, 3)
            max = [math]::Round($workingSet.Maximum / 1MB, 3)
        }
        packets = $last.diagnostics.packetCount - $first.diagnostics.packetCount
        fft = $last.diagnostics.fftCount - $first.diagnostics.fftCount
        draw = $last.diagnostics.drawCount - $first.diagnostics.drawCount
        diagnosticTimeSpanSeconds = ($last.diagnostics.unixTimeMs - $first.diagnostics.unixTimeMs) / 1000
        sampledStates = @($raw.diagnostics.state | Sort-Object -Unique)
        sampledVisibility = @($raw.diagnostics.visible | Sort-Object -Unique)
        sampledCapture = @($raw.diagnostics.capturing | Sort-Object -Unique)
        start = $first
        end = $last
    }
    Save-Evidence "$Phase.samples.json" $raw.ToArray()
    Save-Evidence "$Phase.summary.json" $summary
    Write-Host ('{0} {1:N3}s CPU {2:N4}% private WS max {3:N3} MiB' -f $Phase,$summary.elapsedSeconds,$summary.cpuPercentAllLogicalProcessors,$summary.privateWorkingSetMiB.max)
    return $summary
}

function Initialize-DesktopInspection {
    if (!('RiffloVerificationNative' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'Rifflo.Verification.Native.cs')
    }
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,WindowsBase,System.Drawing.Common
}

function Find-UiaExact([object]$Root, [string]$Value, [string]$Property = 'Name', [int]$ProcessId = 0) {
    $type = [System.Windows.Automation.AutomationElement]
    $propertyId = if ($Property -eq 'AutomationId') { $type::AutomationIdProperty } else { $type::NameProperty }
    $condition = [System.Windows.Automation.PropertyCondition]::new($propertyId, $Value)
    if ($ProcessId) {
        $processCondition = [System.Windows.Automation.PropertyCondition]::new($type::ProcessIdProperty, $ProcessId)
        $condition = [System.Windows.Automation.AndCondition]::new($condition, $processCondition)
    }
    $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-UiaInWindows([object[]]$Windows, [string]$Name) {
    foreach ($window in $Windows) {
        try {
            $element = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$window.Hwnd)
            if ($element.Current.Name -eq $Name) { return $element }
            $match = Find-UiaExact $element $Name
            if ($null -ne $match) { return $match }
        } catch [System.Windows.Automation.ElementNotAvailableException] { }
    }
}

function Find-RiffloWindow([Diagnostics.Process]$Process, [string]$Title, [switch]$IncludeHidden) {
    [RiffloVerificationNative]::Windows($Process.Id) | Where-Object { $_.Title -eq $Title -and ($IncludeHidden -or $_.Visible) } | Select-Object -First 1
}

function Get-UiaClickPoint([object]$Element) {
    $rect = $Element.Current.BoundingRectangle
    if ($rect.IsEmpty -or $rect.Width -le 0 -or $rect.Height -le 0) { throw 'UIA element has no visible bounds.' }
    [pscustomobject]@{ x = [int]($rect.Left + $rect.Width / 2); y = [int]($rect.Top + $rect.Height / 2) }
}

function Invoke-Uia([object]$Element) {
    try {
        $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    } catch [System.InvalidOperationException] {
        $point = Get-UiaClickPoint $Element
        [RiffloVerificationNative]::Click($point.x, $point.y, $false)
    }
}

function Get-SettingControl([object]$Root, [string]$Id) {
    $control = Find-UiaExact $Root $Id 'AutomationId'
    if ($null -eq $control) { throw "The settings control $Id is missing." }
    $control
}

function Select-SettingOption([object]$Root, [string]$Id, [string]$Name) {
    $combo = Get-SettingControl $Root $Id
    $expansion = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expansion.Expand()
    $option = Wait-Observation "$Name in $Id" {
        $item = Find-UiaExact $combo $Name
        if ($null -eq $item) {
            $windows = @([RiffloVerificationNative]::Windows($script:rifflo.Id) | Where-Object Visible)
            $item = Find-UiaInWindows $windows $Name
        }
        $item
    } { $true } 4
    $selection = $null
    $item = $option
    while ($null -ne $item -and $item.Current.ProcessId -eq $script:rifflo.Id) {
        if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) { break }
        $item = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($item)
    }
    if ($null -ne $selection) {
        $selection.Select()
    } else {
        $point = Get-UiaClickPoint $option
        if ([RiffloVerificationNative]::WindowAt($point.x, $point.y).ProcessId -ne $script:rifflo.Id) { throw "The option $Name is obscured." }
        [RiffloVerificationNative]::Click($point.x, $point.y, $false)
    }
    if ($expansion.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) { $expansion.Collapse() }
}

function Set-SettingNumber([object]$Root, [string]$Id, [double]$Value) {
    $control = Get-SettingControl $Root $Id
    $control.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue($Value)
}

function Set-SettingToggle([object]$Root, [string]$Id, [bool]$Enabled) {
    $control = Get-SettingControl $Root $Id
    $toggle = $control.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if (($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) -ne $Enabled) { $toggle.Toggle() }
}

function Get-VisibleTrayIcon([int]$ExplorerProcessId, [string]$Name) {
    $classes = @('Shell_TrayWnd','Shell_SecondaryTrayWnd','TopLevelWindowForOverflowXamlIsland','NotifyIconOverflowWindow')
    $windows = @([RiffloVerificationNative]::Windows($ExplorerProcessId) | Where-Object { $_.Visible -and $_.ClassName -in $classes })
    $icon = Find-UiaInWindows $windows $Name
    if ($null -eq $icon -or $icon.Current.IsOffscreen) { return $null }
    $point = Get-UiaClickPoint $icon
    $hit = [RiffloVerificationNative]::WindowAt($point.x, $point.y)
    if ($hit.ProcessId -eq $ExplorerProcessId -and $hit.ClassName -in $classes) {
        return $icon
    }
    return $null
}

function Find-TrayRifflo {
    $normalizedPath = [IO.Path]::GetFullPath($script:settingsPath).ToUpperInvariant()
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalizedPath)))
    $trayName = 'Rifflo (' + $hash.Substring(0, 8) + ')'
    $tray = [RiffloVerificationNative]::Windows(0) | Where-Object ClassName -EQ 'Shell_TrayWnd' | Select-Object -First 1
    if ($null -ne $tray) {
        $icon = Get-VisibleTrayIcon $tray.ProcessId $trayName
        if ($null -ne $icon) { return $icon }

        $trayRoot = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$tray.Hwnd)
        $button = Find-UiaExact $trayRoot 'SystemTray.ChevronIcon' 'AutomationId'
        if ($null -eq $button) {
            foreach ($label in @('显示隐藏的图标','显示隐藏图标','Show hidden icons','Hidden icons','隐藏的图标')) {
                $button = Find-UiaExact $trayRoot $label
                if ($null -ne $button) { break }
            }
        }
        if ($null -ne $button) {
            $point = Get-UiaClickPoint $button
            [RiffloVerificationNative]::Click($point.x, $point.y, $false)
            Start-Sleep -Milliseconds 450
            return Wait-Observation "$trayName in tray overflow" { Get-VisibleTrayIcon $tray.ProcessId $trayName } { $true } 5
        }
    }

    throw "$trayName could not be found in the tray or notification overflow."
}

function Invoke-RiffloTray([string]$Label) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $icon = Find-TrayRifflo
            $point = Get-UiaClickPoint $icon
            [RiffloVerificationNative]::Click($point.x, $point.y, $true)
            $menu = Wait-Observation "Rifflo tray menu: $Label" {
                $windows = @([RiffloVerificationNative]::Windows($script:rifflo.Id) | Where-Object Visible)
                Find-UiaInWindows $windows $Label
            } { $true } 2
            $point = Get-UiaClickPoint $menu
            $hit = [RiffloVerificationNative]::WindowAt($point.x, $point.y)
            if ($hit.ProcessId -ne $script:rifflo.Id) { throw "The test instance's tray menu is obscured." }
            [RiffloVerificationNative]::Click($point.x, $point.y, $false)
            return
        } catch {
            if ($attempt -eq 3) { throw }
        }
    }
}

function Save-ScreenCrop([string]$FileName, [int]$X, [int]$Y, [int]$Width, [int]$Height) {
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($X, $Y, 0, 0, $bitmap.Size) }
        finally { $graphics.Dispose() }
        $bitmap.Save((Join-Path $EvidenceDirectory $FileName), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

<#
    Update Tracker - a small Windows 11 app that keeps your programs up to date.

      * Pick the installed apps you care about (G HUB, NZXT CAM, Steam, WinRAR, ...).
      * It checks them with winget (built into Windows 11) on a schedule, even when
        those apps never run at startup.
      * Shows installed version -> new version, the release date (when the winget
        manifest lists one), the day the update was first spotted, and when the
        app was installed or last updated.
      * One-click Update per app, Update all, and a per-app Auto switch that
        installs new versions by itself as soon as they are found.
      * Lives in the tray. "Start with Windows" adds a Task Scheduler entry that
        starts it at sign-in with admin rights, so auto-updates install silently.

    Run:   double-click "Start Update Tracker.cmd" (keep both files in the same folder)
    Data:  %APPDATA%\UpdateTracker  (config.json, update-log.txt)
#>
[CmdletBinding()]
param(
    [switch]$Tray,
    [switch]$RegisterStartup,
    [switch]$UnregisterStartup,
    [string]$ForUser
)

$ErrorActionPreference = 'Continue'

# ================================================================ paths and constants
$script:AppTitle   = 'Update Tracker'
$script:TaskName   = 'UpdateTracker'
$script:ScriptPath = $PSCommandPath
$script:DataDir    = Join-Path $env:APPDATA 'UpdateTracker'
$script:ConfigPath = Join-Path $script:DataDir 'config.json'
$script:LogPath    = Join-Path $script:DataDir 'update-log.txt'
$script:ShowFlag   = Join-Path $script:DataDir 'show.flag'
$script:IconPath   = Join-Path $script:DataDir 'UpdateTracker.ico'
$script:PsExe      = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:Inv        = [System.Globalization.CultureInfo]::InvariantCulture
$script:Arrow      = [string][char]0x2192
$script:Dash       = [string][char]0x2014

if (-not (Test-Path -LiteralPath $script:DataDir)) {
    New-Item -ItemType Directory -Path $script:DataDir -Force | Out-Null
}

function Write-Log {
    param([string]$Message)
    try {
        $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
        Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8
    } catch { }
}

# ================================================================ startup task (Task Scheduler)
function Get-CurrentUserName { [System.Security.Principal.WindowsIdentity]::GetCurrent().Name }

function Test-IsAdmin {
    $principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
    $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-StartupTask {
    [bool](Get-ScheduledTask -TaskName $script:TaskName -ErrorAction SilentlyContinue)
}

function Register-StartupTask {
    param([string]$User)
    $argLine   = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -Tray' -f $script:ScriptPath
    $action    = New-ScheduledTaskAction -Execute $script:PsExe -Argument $argLine -WorkingDirectory (Split-Path -Parent $script:ScriptPath)
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $User
    $trigger.Delay = 'PT30S'
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $script:TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
        -Description 'Update Tracker: checks your apps for updates and installs the ones set to Auto.' -Force | Out-Null
}

# Elevated helper mode: called by the app itself (through a UAC prompt) to add/remove the task.
if ($RegisterStartup -or $UnregisterStartup) {
    try {
        if ($RegisterStartup) {
            if (-not $ForUser) { $ForUser = Get-CurrentUserName }
            Register-StartupTask -User $ForUser
            Write-Log "Startup task registered for $ForUser."
        } else {
            Unregister-ScheduledTask -TaskName $script:TaskName -Confirm:$false -ErrorAction Stop
            Write-Log 'Startup task removed.'
        }
        exit 0
    } catch {
        Write-Log ('Startup task change failed: ' + $_.Exception.Message)
        exit 1
    }
}

# ================================================================ single instance
$script:Mutex = $null
$createdNew = $false
try {
    $script:Mutex = [System.Threading.Mutex]::new($true, 'Local\UpdateTracker_SingleInstance', [ref]$createdNew)
} catch {
    $createdNew = $false   # it exists and belongs to an elevated copy
}
if (-not $createdNew) {
    # Another copy is running: ask it to show its window, then quit.
    if (-not $Tray) { try { Set-Content -LiteralPath $script:ShowFlag -Value 'show' -Force } catch { } }
    exit 0
}

try {
    if ((Test-Path -LiteralPath $script:LogPath) -and (Get-Item -LiteralPath $script:LogPath).Length -gt 2MB) {
        Move-Item -LiteralPath $script:LogPath -Destination ($script:LogPath + '.old') -Force
    }
} catch { }

# ================================================================ worker functions
# These run in a background runspace so the window never freezes. Keep them self-contained.
$script:WorkerFunctionNames = @(
    'Get-WinGetExe', 'Invoke-WinGet', 'Get-WinGetMessage', 'Initialize-WinGetModule',
    'Get-ReleaseDate', 'Get-ArpEntries', 'Find-InstallDate'
)

function Get-WinGetExe {
    $cmd = Get-Command -Name 'winget.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Path }
    $alias = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
    if (Test-Path -LiteralPath $alias) { return $alias }
    throw 'winget was not found. Install or update "App Installer" from the Microsoft Store, then try again.'
}

function Invoke-WinGet {
    # Runs winget hidden, output goes to a temp file (safe even if an installer relaunches its app).
    param([string[]]$Arguments, [int]$TimeoutSec = 1800)
    $exe = Get-WinGetExe
    $tmp = Join-Path $env:TEMP ('updatetracker_' + [guid]::NewGuid().ToString('N') + '.txt')
    $argLine = ($Arguments | ForEach-Object { if ($_ -match '[\s&|<>^]') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $env:SystemRoot 'System32\cmd.exe'
    $psi.Arguments = '/d /s /c ""' + $exe + '" ' + $argLine + ' > "' + $tmp + '" 2>&1"'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $timedOut = -not $proc.WaitForExit($TimeoutSec * 1000)
    if ($timedOut) { try { $proc.Kill() } catch { } }
    $code = if ($timedOut) { -1 } else { $proc.ExitCode }
    $text = ''
    if (Test-Path -LiteralPath $tmp) {
        try {
            $fs = [System.IO.File]::Open($tmp, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            $reader = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8)
            $text = $reader.ReadToEnd()
            $reader.Close()
        } catch { }
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
    if ($timedOut) { $text += "`nStopped waiting after $TimeoutSec seconds." }
    [pscustomobject]@{ ExitCode = $code; Output = $text; TimedOut = $timedOut }
}

function Get-WinGetMessage {
    param($Result)
    $code = [int]$Result.ExitCode
    $known = @{}
    $known[-1978335189] = 'No newer version applies. It may already be updated.'
    $known[-1978334975] = 'The app is running. Close it and try again.'
    $known[-1978334974] = 'Another install is in progress. Try again in a few minutes.'
    $known[-1978334967] = 'Installed. Restart your PC to finish.'
    $known[-1978334964] = 'Cancelled. The permission prompt was probably declined.'
    if ($Result.TimedOut) { return 'The installer took longer than 30 minutes and was left running.' }
    if ($known.ContainsKey($code)) { return $known[$code] }
    $lines = @($Result.Output -split "[\r\n]+" | ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and $_ -match '[A-Za-z]{3}' -and $_ -notmatch '^[\-\\\|/ ]+$' })
    $last = if ($lines.Count) { $lines[$lines.Count - 1] } else { 'winget reported an error.' }
    '{0} (code 0x{1:X8})' -f $last, $code
}

function Initialize-WinGetModule {
    param($Sync)
    if (Get-Module -Name Microsoft.WinGet.Client) { return }
    if (-not (Get-Module -ListAvailable -Name Microsoft.WinGet.Client)) {
        if ($Sync) { $Sync.Status = 'First run: installing the Microsoft.WinGet.Client PowerShell module. This happens once and takes about a minute.' }
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        if (-not (Get-PackageProvider -ListAvailable -Name NuGet -ErrorAction SilentlyContinue)) {
            Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Scope CurrentUser -Force | Out-Null
        }
        Install-Module -Name Microsoft.WinGet.Client -Scope CurrentUser -Force -AllowClobber -Repository PSGallery -ErrorAction Stop
    }
    Import-Module -Name Microsoft.WinGet.Client -ErrorAction Stop
}

function Get-ReleaseDate {
    param([string]$Id, [string]$Version, [string]$Source)
    if ($Source -ne 'winget' -or -not $Version) { return '' }
    $r = Invoke-WinGet -Arguments @('show', '--id', $Id, '--exact', '--version', $Version, '--source', 'winget',
                                    '--accept-source-agreements', '--disable-interactivity') -TimeoutSec 120
    $m = [regex]::Match($r.Output, '(?im)^\s*Release Date:\s*(\d{4}-\d{2}-\d{2})')
    if ($m.Success) { return $m.Groups[1].Value }
    # Non-English Windows: take a "Label: yyyy-mm-dd" line.
    $m = [regex]::Match($r.Output, '(?m)^\s*[^:\r\n]{3,40}:\s*(\d{4}-\d{2}-\d{2})\s*$')
    if ($m.Success) { return $m.Groups[1].Value }
    ''
}

function Get-ArpEntries {
    $paths = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($path in $paths) {
        Get-ItemProperty -Path $path -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -and $_.InstallDate } |
            ForEach-Object { [pscustomobject]@{ Name = [string]$_.DisplayName; InstallDate = [string]$_.InstallDate } }
    }
}

function Find-InstallDate {
    param($Arp, [string]$Name)
    if (-not $Name) { return '' }
    $hit = $null
    foreach ($e in $Arp) { if ($e.Name -eq $Name) { $hit = $e; break } }
    if (-not $hit) {
        foreach ($e in $Arp) { if ($e.Name.StartsWith($Name, [System.StringComparison]::OrdinalIgnoreCase)) { $hit = $e; break } }
    }
    if (-not $hit) { return '' }
    $digits = $hit.InstallDate -replace '[^0-9]', ''
    $d = [datetime]::MinValue
    if ($digits.Length -ge 8 -and [datetime]::TryParseExact($digits.Substring(0, 8), 'yyyyMMdd',
            [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$d)) {
        return $d.ToString('yyyy-MM-dd')
    }
    ''
}

# ---------------------------------------------------------------- background jobs
$script:CheckJob = {
    param($TrackedIds, $DateCache, $Sync)
    try {
        Initialize-WinGetModule -Sync $Sync
        $Sync.Status = 'Refreshing the winget catalog...'
        $null = Invoke-WinGet -Arguments @('list', '--id', 'Microsoft.AppInstaller', '--exact',
                                           '--accept-source-agreements', '--disable-interactivity') -TimeoutSec 180
        $Sync.Status = 'Checking your apps for updates...'
        $pkgs = @(Get-WinGetPackage -ErrorAction Stop)
        $all = New-Object System.Collections.ArrayList
        foreach ($p in $pkgs) {
            $ver = if ($p.PSObject.Properties['InstalledVersion']) { [string]$p.InstalledVersion } else { [string]$p.Version }
            $avail = ''
            if ($p.IsUpdateAvailable) {
                $versions = @($p.AvailableVersions)
                if ($versions.Count) { $avail = [string]$versions[0] }
            }
            [void]$all.Add([pscustomobject]@{
                Id = [string]$p.Id; Name = [string]$p.Name; Version = $ver; Available = $avail; Source = [string]$p.Source
            })
        }
        $tracked = @($all | Where-Object { $TrackedIds -contains $_.Id })
        $dates = @{}
        foreach ($t in $tracked) {
            if (-not $t.Available) { continue }
            $key = $t.Id + '|' + $t.Available
            if (-not $DateCache.ContainsKey($key)) {
                $Sync.Status = "Looking up the release date for $($t.Name)..."
                $dates[$key] = Get-ReleaseDate -Id $t.Id -Version $t.Available -Source $t.Source
            }
        }
        $arp = @(Get-ArpEntries)
        $installDates = @{}
        foreach ($t in $tracked) {
            $d = Find-InstallDate -Arp $arp -Name $t.Name
            if ($d) { $installDates[$t.Id] = $d }
        }
        [pscustomobject]@{ Ok = $true; All = $all.ToArray(); Dates = $dates; InstallDates = $installDates; Error = '' }
    } catch {
        [pscustomobject]@{ Ok = $false; Error = $_.Exception.Message }
    }
}

$script:UpdateJob = {
    param($Items, $Sync)
    $results = New-Object System.Collections.ArrayList
    $n = @($Items).Count
    $i = 0
    foreach ($it in $Items) {
        $i++
        $Sync.Current = $it.Id
        $Sync.Status = "Installing $($it.Name) $($it.To) ($i of $n). Installers can take a minute."
        $a = @('upgrade', '--id', $it.Id, '--exact', '--silent', '--accept-package-agreements',
               '--accept-source-agreements', '--disable-interactivity')
        if ($it.Source) { $a += @('--source', $it.Source) }
        try {
            $r = Invoke-WinGet -Arguments $a -TimeoutSec 1800
            $code = [int]$r.ExitCode
            $ok = ($code -eq 0 -or $code -eq -1978334967)
            $msg = if ($code -eq 0) { 'Updated' } else { Get-WinGetMessage -Result $r }
            [void]$results.Add([pscustomobject]@{
                Id = $it.Id; Name = $it.Name; From = $it.From; To = $it.To; Ok = $ok; Code = $code; Message = $msg; Output = $r.Output
            })
        } catch {
            [void]$results.Add([pscustomobject]@{
                Id = $it.Id; Name = $it.Name; From = $it.From; To = $it.To; Ok = $false; Code = -1; Message = $_.Exception.Message; Output = ''
            })
        }
    }
    $Sync.Current = $null
    [pscustomobject]@{ Results = $results.ToArray() }
}

# ================================================================ UI setup
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
try {
    Add-Type -Namespace UpdTracker -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("dwmapi.dll")]
public static extern int DwmSetWindowAttribute(System.IntPtr hwnd, int attr, ref int value, int size);
'@
} catch { }

$script:App = New-Object System.Windows.Application
$script:App.ShutdownMode = [System.Windows.ShutdownMode]::OnExplicitShutdown
$script:App.Add_DispatcherUnhandledException({
    param($s, $e)
    Write-Log ('UI error: ' + $e.Exception.ToString())
    $e.Handled = $true
})

# ---------------------------------------------------------------- shared styles
$script:StylesXaml = @'
<Style x:Key="FocusRing">
  <Setter Property="Control.Template">
    <Setter.Value>
      <ControlTemplate>
        <Border BorderBrush="#F2A93B" BorderThickness="2" CornerRadius="8" Margin="-3"/>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>

<Style x:Key="Btn" TargetType="Button">
  <Setter Property="Foreground" Value="#E8EAED"/>
  <Setter Property="Background" Value="#262A32"/>
  <Setter Property="BorderBrush" Value="#353B45"/>
  <Setter Property="Padding" Value="16,8"/>
  <Setter Property="FontWeight" Value="SemiBold"/>
  <Setter Property="Cursor" Value="Hand"/>
  <Setter Property="FocusVisualStyle" Value="{StaticResource FocusRing}"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="Button">
        <Grid x:Name="Root">
          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="6"/>
          <Border x:Name="Hl" Background="#FFFFFF" Opacity="0" CornerRadius="6"/>
          <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Hl" Property="Opacity" Value="0.08"/></Trigger>
          <Trigger Property="IsPressed" Value="True"><Setter TargetName="Hl" Property="Opacity" Value="0.16"/></Trigger>
          <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Root" Property="Opacity" Value="0.4"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>

<Style x:Key="BtnPrimary" TargetType="Button" BasedOn="{StaticResource Btn}">
  <Setter Property="Background" Value="#F2A93B"/>
  <Setter Property="BorderBrush" Value="#F2A93B"/>
  <Setter Property="Foreground" Value="#1A1D23"/>
</Style>

<Style x:Key="BtnSmall" TargetType="Button" BasedOn="{StaticResource BtnPrimary}">
  <Setter Property="Padding" Value="14,5"/>
  <Setter Property="FontSize" Value="12"/>
</Style>

<Style x:Key="BtnGhost" TargetType="Button" BasedOn="{StaticResource Btn}">
  <Setter Property="Background" Value="Transparent"/>
  <Setter Property="BorderBrush" Value="Transparent"/>
  <Setter Property="Foreground" Value="#8F96A3"/>
  <Setter Property="Padding" Value="10,6"/>
  <Setter Property="FontWeight" Value="Normal"/>
</Style>

<Style x:Key="Switch" TargetType="CheckBox">
  <Setter Property="Foreground" Value="#C9CDD4"/>
  <Setter Property="Cursor" Value="Hand"/>
  <Setter Property="VerticalAlignment" Value="Center"/>
  <Setter Property="FocusVisualStyle" Value="{StaticResource FocusRing}"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="CheckBox">
        <StackPanel x:Name="Root" Orientation="Horizontal" Background="Transparent">
          <Border x:Name="Track" Width="36" Height="20" CornerRadius="10" Background="#262A32" BorderBrush="#454C57" BorderThickness="1" VerticalAlignment="Center">
            <Ellipse x:Name="Knob" Width="12" Height="12" Fill="#8F96A3" HorizontalAlignment="Left" VerticalAlignment="Center" Margin="3,0,0,0"/>
          </Border>
          <ContentPresenter Margin="9,0,0,0" VerticalAlignment="Center"/>
        </StackPanel>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Track" Property="BorderBrush" Value="#7FB59A"/></Trigger>
          <Trigger Property="IsChecked" Value="True">
            <Setter TargetName="Track" Property="Background" Value="#7FB59A"/>
            <Setter TargetName="Track" Property="BorderBrush" Value="#7FB59A"/>
            <Setter TargetName="Knob" Property="Fill" Value="#1A1D23"/>
            <Setter TargetName="Knob" Property="HorizontalAlignment" Value="Right"/>
            <Setter TargetName="Knob" Property="Margin" Value="0,0,3,0"/>
          </Trigger>
          <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Root" Property="Opacity" Value="0.4"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>

<Style x:Key="Pill" TargetType="RadioButton">
  <Setter Property="Foreground" Value="#8F96A3"/>
  <Setter Property="Cursor" Value="Hand"/>
  <Setter Property="FocusVisualStyle" Value="{StaticResource FocusRing}"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="RadioButton">
        <Border x:Name="Bd" Background="Transparent" BorderBrush="#353B45" BorderThickness="1" CornerRadius="6" Padding="10,4" Margin="0,0,4,0">
          <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="#5A616D"/></Trigger>
          <Trigger Property="IsChecked" Value="True">
            <Setter TargetName="Bd" Property="Background" Value="#353B45"/>
            <Setter TargetName="Bd" Property="BorderBrush" Value="#5A616D"/>
            <Setter Property="Foreground" Value="#E8EAED"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>

<Style x:Key="Check" TargetType="CheckBox">
  <Setter Property="Foreground" Value="#E8EAED"/>
  <Setter Property="Cursor" Value="Hand"/>
  <Setter Property="FocusVisualStyle" Value="{StaticResource FocusRing}"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="CheckBox">
        <Border x:Name="Row" Background="Transparent" CornerRadius="6" Padding="12,8">
          <Grid>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto"/>
              <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Border x:Name="Box" Width="18" Height="18" CornerRadius="4" BorderBrush="#4A515D" BorderThickness="1.5" Background="Transparent" VerticalAlignment="Center">
              <Path x:Name="Mark" Data="M 3,7.5 L 6.5,11 L 12.5,4" Stroke="#1A1D23" StrokeThickness="2"
                    StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" Visibility="Collapsed"/>
            </Border>
            <ContentPresenter Grid.Column="1" Margin="14,0,0,0" VerticalAlignment="Center"/>
          </Grid>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="#262A32"/></Trigger>
          <Trigger Property="IsChecked" Value="True">
            <Setter TargetName="Box" Property="Background" Value="#F2A93B"/>
            <Setter TargetName="Box" Property="BorderBrush" Value="#F2A93B"/>
            <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>

<Style x:Key="SlimScroll" TargetType="ScrollBar">
  <Setter Property="Width" Value="10"/>
  <Setter Property="MinWidth" Value="10"/>
  <Setter Property="Template">
    <Setter.Value>
      <ControlTemplate TargetType="ScrollBar">
        <Track x:Name="PART_Track" IsDirectionReversed="True" Margin="2,4,2,4">
          <Track.Thumb>
            <Thumb>
              <Thumb.Template>
                <ControlTemplate TargetType="Thumb">
                  <Border Background="#3E444F" CornerRadius="3"/>
                </ControlTemplate>
              </Thumb.Template>
            </Thumb>
          </Track.Thumb>
        </Track>
        <ControlTemplate.Triggers>
          <Trigger Property="IsEnabled" Value="False"><Setter TargetName="PART_Track" Property="Visibility" Value="Hidden"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>
'@

# ---------------------------------------------------------------- main window
$script:MainXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Update Tracker" Width="1140" Height="700" MinWidth="1020" MinHeight="460"
        WindowStartupLocation="CenterScreen" Background="#1A1D23"
        FontFamily="Segoe UI Variable Text, Segoe UI" FontSize="13" Foreground="#E8EAED"
        UseLayoutRounding="True" SnapsToDevicePixels="True">
  <Window.Resources>
    %STYLES%
  </Window.Resources>
  <Grid>
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>

    <Grid Grid.Row="0" Margin="28,24,28,6">
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
      </Grid.ColumnDefinitions>
      <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <Image x:Name="Logo" Width="36" Height="36" Margin="0,0,14,0" VerticalAlignment="Center"/>
        <StackPanel VerticalAlignment="Center">
          <TextBlock Text="Update Tracker" FontSize="24" FontWeight="SemiBold" FontFamily="Segoe UI Variable Display, Segoe UI"/>
          <TextBlock x:Name="SubTitle" Text="Not checked yet." Foreground="#8F96A3" Margin="0,2,0,0"/>
        </StackPanel>
      </StackPanel>
      <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
        <Button x:Name="BtnAdd" Style="{StaticResource Btn}" Content="Add apps" Margin="0,0,8,0"/>
        <Button x:Name="BtnCheck" Style="{StaticResource Btn}" Content="Check now" Margin="0,0,8,0"/>
        <Button x:Name="BtnUpdateAll" Style="{StaticResource BtnPrimary}" Content="Update all"/>
      </StackPanel>
    </Grid>

    <StackPanel Grid.Row="1" Margin="28,10,28,14">
      <TextBlock x:Name="StatusText" Foreground="#8F96A3" FontSize="12" TextTrimming="CharacterEllipsis"/>
      <ProgressBar x:Name="Busy" IsIndeterminate="True" Height="2" Margin="0,8,0,0" Foreground="#F2A93B"
                   Background="Transparent" BorderThickness="0" Visibility="Hidden"/>
    </StackPanel>

    <Border Grid.Row="2" Margin="28,0,28,20" Background="#20242B" CornerRadius="10">
      <Grid>
        <Grid.RowDefinitions>
          <RowDefinition Height="Auto"/>
          <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
        <Grid Grid.Row="0" Margin="18,12,28,10" TextBlock.Foreground="#6F7682" TextBlock.FontSize="12">
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*" MinWidth="200"/>
            <ColumnDefinition Width="150"/>
            <ColumnDefinition Width="216"/>
            <ColumnDefinition Width="180"/>
            <ColumnDefinition Width="64"/>
            <ColumnDefinition Width="96"/>
            <ColumnDefinition Width="36"/>
          </Grid.ColumnDefinitions>
          <TextBlock Grid.Column="0" Text="App"/>
          <TextBlock Grid.Column="1" Text="Installed"/>
          <TextBlock Grid.Column="2" Text="Available"/>
          <TextBlock Grid.Column="3" Text="Status"/>
          <TextBlock Grid.Column="4" Text="Auto"/>
        </Grid>
        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Visible" HorizontalScrollBarVisibility="Disabled">
          <ScrollViewer.Resources>
            <Style TargetType="ScrollBar" BasedOn="{StaticResource SlimScroll}"/>
          </ScrollViewer.Resources>
          <ItemsControl x:Name="List" Focusable="False">
            <ItemsControl.ItemTemplate>
              <DataTemplate>
                <Border BorderBrush="#2B3038" BorderThickness="0,1,0,0">
                  <Grid>
                    <Rectangle Width="3" RadiusX="1.5" RadiusY="1.5" Fill="#F2A93B" HorizontalAlignment="Left"
                               Margin="0,16,0,16" Visibility="{Binding EdgeVis}"/>
                    <Grid Margin="18,14,18,14">
                      <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" MinWidth="200"/>
                        <ColumnDefinition Width="150"/>
                        <ColumnDefinition Width="216"/>
                        <ColumnDefinition Width="180"/>
                        <ColumnDefinition Width="64"/>
                        <ColumnDefinition Width="96"/>
                        <ColumnDefinition Width="36"/>
                      </Grid.ColumnDefinitions>

                      <StackPanel Grid.Column="0" VerticalAlignment="Center" Margin="0,0,12,0">
                        <TextBlock Text="{Binding Name}" FontSize="14" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
                        <TextBlock Text="{Binding Id}" FontSize="12" Foreground="#6F7682" TextTrimming="CharacterEllipsis" Margin="0,3,0,0"/>
                      </StackPanel>

                      <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="0,0,10,0">
                        <TextBlock Text="{Binding Installed}" FontSize="14" Typography.NumeralAlignment="Tabular" TextTrimming="CharacterEllipsis"/>
                        <TextBlock Text="{Binding InstalledSub}" FontSize="12" Foreground="#8F96A3" Margin="0,3,0,0"/>
                      </StackPanel>

                      <StackPanel Grid.Column="2" VerticalAlignment="Center" Margin="0,0,10,0">
                        <TextBlock FontSize="14" Typography.NumeralAlignment="Tabular" TextTrimming="CharacterEllipsis"><Run Text="{Binding ArrowText, Mode=OneWay}" Foreground="#6F7682"/><Run Text="{Binding AvailSame, Mode=OneWay}"/><Run Text="{Binding AvailDiff, Mode=OneWay}" Foreground="#F2A93B" FontWeight="SemiBold"/></TextBlock>
                        <TextBlock Text="{Binding AvailableSub}" FontSize="12" Foreground="#8F96A3" Margin="0,3,0,0" LineHeight="17"/>
                      </StackPanel>

                      <StackPanel Grid.Column="3" VerticalAlignment="Center" Background="Transparent" ToolTip="{Binding StatusTip}" Margin="0,0,10,0">
                        <StackPanel Orientation="Horizontal">
                          <Ellipse Width="8" Height="8" Fill="{Binding StatusColor}" VerticalAlignment="Center" Margin="0,1,8,0"/>
                          <TextBlock Text="{Binding Status}" Foreground="{Binding StatusColor}" FontWeight="SemiBold"/>
                        </StackPanel>
                        <TextBlock Text="{Binding StatusSub}" FontSize="12" Foreground="#8F96A3" Margin="16,3,0,0" TextTrimming="CharacterEllipsis"/>
                      </StackPanel>

                      <CheckBox Grid.Column="4" Style="{StaticResource Switch}" Tag="auto" IsChecked="{Binding Auto, Mode=OneWay}"
                                ToolTip="Install new versions of this app automatically"/>

                      <Button Grid.Column="5" Style="{StaticResource BtnSmall}" Tag="update" Content="{Binding UpdateLabel}"
                              Visibility="{Binding UpdateVis}" IsEnabled="{Binding CanUpdate}"
                              HorizontalAlignment="Left" VerticalAlignment="Center"/>

                      <Button Grid.Column="6" Style="{StaticResource BtnGhost}" Tag="remove" Content="&#x2715;" Padding="8,4"
                              ToolTip="Stop tracking this app" VerticalAlignment="Center" HorizontalAlignment="Right"/>
                    </Grid>
                  </Grid>
                </Border>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </ScrollViewer>

        <StackPanel x:Name="Empty" Grid.Row="1" VerticalAlignment="Center" HorizontalAlignment="Center" Visibility="Collapsed">
          <TextBlock Text="No apps tracked yet" FontSize="17" FontWeight="SemiBold" HorizontalAlignment="Center"/>
          <TextBlock Text="Choose Add apps to pick the programs you want to keep up to date." Foreground="#8F96A3"
                     Margin="0,6,0,0" HorizontalAlignment="Center"/>
        </StackPanel>
      </Grid>
    </Border>

    <Border Grid.Row="3" BorderBrush="#2B3038" BorderThickness="0,1,0,0" Padding="28,12,28,12">
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width="*"/>
          <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <WrapPanel VerticalAlignment="Center">
          <TextBlock Text="Check every" Foreground="#8F96A3" VerticalAlignment="Center" Margin="0,0,10,0"/>
          <RadioButton x:Name="Int1"  GroupName="Interval" Style="{StaticResource Pill}" Content="1 h"  Tag="1"/>
          <RadioButton x:Name="Int3"  GroupName="Interval" Style="{StaticResource Pill}" Content="3 h"  Tag="3"/>
          <RadioButton x:Name="Int6"  GroupName="Interval" Style="{StaticResource Pill}" Content="6 h"  Tag="6"/>
          <RadioButton x:Name="Int12" GroupName="Interval" Style="{StaticResource Pill}" Content="12 h" Tag="12"/>
          <RadioButton x:Name="Int24" GroupName="Interval" Style="{StaticResource Pill}" Content="24 h" Tag="24"/>
          <CheckBox x:Name="ChkStartup" Style="{StaticResource Switch}" Content="Start with Windows" Margin="24,0,0,0"
                    ToolTip="Starts in the tray when you sign in, with admin rights, so auto-updates install without permission prompts."/>
          <CheckBox x:Name="ChkTray" Style="{StaticResource Switch}" Content="Keep running in the tray" Margin="20,0,0,0"
                    ToolTip="Closing the window keeps Update Tracker checking in the background."/>
          <CheckBox x:Name="ChkNotify" Style="{StaticResource Switch}" Content="Notifications" Margin="20,0,0,0"/>
        </WrapPanel>
        <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
          <Button x:Name="BtnShortcut" Style="{StaticResource BtnGhost}" Content="Add shortcuts" Margin="0,0,4,0"
                  ToolTip="Adds Update Tracker to your desktop and Start menu"/>
          <Button x:Name="BtnLog" Style="{StaticResource BtnGhost}" Content="Open log"/>
        </StackPanel>
      </Grid>
    </Border>
  </Grid>
</Window>
'@

# ---------------------------------------------------------------- app picker window
$script:PickerXaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Choose apps to track" Width="660" Height="720" MinHeight="420" MinWidth="520"
        WindowStartupLocation="CenterOwner" Background="#1A1D23" Foreground="#E8EAED"
        FontFamily="Segoe UI Variable Text, Segoe UI" FontSize="13" ShowInTaskbar="False"
        UseLayoutRounding="True" SnapsToDevicePixels="True">
  <Window.Resources>
    %STYLES%
  </Window.Resources>
  <Grid Margin="24,20,24,20">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>
    <StackPanel Grid.Row="0">
      <TextBlock Text="Choose apps to track" FontSize="20" FontWeight="SemiBold" FontFamily="Segoe UI Variable Display, Segoe UI"/>
      <TextBlock x:Name="Intro" TextWrapping="Wrap" Foreground="#8F96A3" Margin="0,6,0,14" LineHeight="19"/>
    </StackPanel>
    <Border Grid.Row="1" Background="#20242B" BorderBrush="#353B45" BorderThickness="1" CornerRadius="8" Padding="12,8" Margin="0,0,0,12">
      <Grid>
        <TextBlock x:Name="Hint" Text="Search by name or id" Foreground="#6F7682" IsHitTestVisible="False" VerticalAlignment="Center" Margin="2,0,0,0"/>
        <TextBox x:Name="Search" Background="Transparent" BorderThickness="0" Foreground="#E8EAED" CaretBrush="#E8EAED"
                 FontSize="13" VerticalAlignment="Center"/>
      </Grid>
    </Border>
    <Border Grid.Row="2" Background="#20242B" CornerRadius="10" Padding="4">
      <ScrollViewer VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
        <ScrollViewer.Resources>
          <Style TargetType="ScrollBar" BasedOn="{StaticResource SlimScroll}"/>
        </ScrollViewer.Resources>
        <StackPanel x:Name="Items" Margin="2"/>
      </ScrollViewer>
    </Border>
    <Grid Grid.Row="3" Margin="0,16,0,0">
      <TextBlock x:Name="Count" Foreground="#8F96A3" VerticalAlignment="Center"/>
      <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
        <Button x:Name="BtnCancel" Style="{StaticResource Btn}" Content="Cancel" Margin="0,0,8,0" IsCancel="True"/>
        <Button x:Name="BtnOk" Style="{StaticResource BtnPrimary}" Content="Track selected apps" IsDefault="True"/>
      </StackPanel>
    </Grid>
  </Grid>
</Window>
'@

# ================================================================ helpers (UI thread)
function ConvertTo-Plain {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) { return $Value }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        $h = @{}
        foreach ($prop in $Value.PSObject.Properties) { $h[$prop.Name] = ConvertTo-Plain $prop.Value }
        return $h
    }
    if ($Value -is [string]) { return $Value }
    if ($Value -is [System.Collections.IEnumerable]) {
        $list = New-Object System.Collections.ArrayList
        foreach ($item in $Value) { [void]$list.Add((ConvertTo-Plain $item)) }
        return ,$list
    }
    return $Value
}

function Read-Config {
    $cfg = @{
        Apps = (New-Object System.Collections.ArrayList); IntervalHours = 6; KeepInTray = $true; Notify = $true
        LastCheck = ''; SetupDone = $false
        ReleaseDates = @{}; Detected = @{}; Notified = @{}; LastUpdated = @{}; AutoAttempts = @{}
    }
    if (Test-Path -LiteralPath $script:ConfigPath) {
        try {
            $raw = Get-Content -LiteralPath $script:ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $loaded = ConvertTo-Plain $raw
            foreach ($k in @($loaded.Keys)) {
                if ($cfg.ContainsKey($k) -and $null -ne $loaded[$k]) { $cfg[$k] = $loaded[$k] }
            }
        } catch {
            Write-Log ('Settings file could not be read, starting fresh: ' + $_.Exception.Message)
            try { Copy-Item -LiteralPath $script:ConfigPath -Destination ($script:ConfigPath + '.broken') -Force } catch { }
        }
    }
    $apps = New-Object System.Collections.ArrayList
    foreach ($a in @($cfg.Apps)) {
        if ($a -is [System.Collections.IDictionary] -and $a['Id']) {
            [void]$apps.Add(@{ Id = [string]$a['Id']; Name = [string]$a['Name']; Auto = [bool]$a['Auto'] })
        }
    }
    $cfg.Apps = $apps
    foreach ($k in 'ReleaseDates', 'Detected', 'Notified', 'LastUpdated', 'AutoAttempts') {
        if ($cfg[$k] -isnot [System.Collections.IDictionary]) { $cfg[$k] = @{} }
    }
    $hours = 6
    [void][int]::TryParse([string]$cfg.IntervalHours, [ref]$hours)
    if (@(1, 3, 6, 12, 24) -notcontains $hours) { $hours = 6 }
    $cfg.IntervalHours = $hours
    $cfg.KeepInTray = [bool]$cfg.KeepInTray
    $cfg.Notify = [bool]$cfg.Notify
    $cfg.SetupDone = [bool]$cfg.SetupDone
    $cfg.LastCheck = [string]$cfg.LastCheck
    $cfg
}

function Save-Config {
    try {
        $json = $script:Config | ConvertTo-Json -Depth 6
        $tmp = $script:ConfigPath + '.tmp'
        [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $tmp -Destination $script:ConfigPath -Force
    } catch {
        Write-Log ('Could not save settings: ' + $_.Exception.Message)
    }
}

function Get-Brush {
    param([string]$Hex)
    $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($Hex))
    $b.Freeze()
    $b
}

function Format-Day {
    param([string]$Iso)
    if (-not $Iso) { return '' }
    $d = [datetime]::MinValue
    $s = $Iso.Substring(0, [Math]::Min(10, $Iso.Length))
    if ([datetime]::TryParseExact($s, 'yyyy-MM-dd', $script:Inv, [System.Globalization.DateTimeStyles]::None, [ref]$d)) {
        if ($d.Date -eq (Get-Date).Date) { return 'today' }
        if ($d.Date -eq (Get-Date).Date.AddDays(-1)) { return 'yesterday' }
        return $d.ToString('d MMM yyyy', $script:Inv)
    }
    $Iso
}

function Format-When {
    param([datetime]$When)
    $t = $When.ToString('HH:mm', $script:Inv)
    if ($When.Date -eq (Get-Date).Date) { return "today at $t" }
    if ($When.Date -eq (Get-Date).Date.AddDays(-1)) { return "yesterday at $t" }
    $When.ToString('d MMM', $script:Inv) + " at $t"
}

function Get-LastCheckTime {
    $d = [datetime]::MinValue
    if ($script:Config.LastCheck -and [datetime]::TryParseExact($script:Config.LastCheck, 'yyyy-MM-dd HH:mm:ss', $script:Inv,
            [System.Globalization.DateTimeStyles]::None, [ref]$d)) { return $d }
    $null
}

function Split-VersionDiff {
    # "2024.3.5" -> "2024.6.1"  gives  @("2024.", "6.1")  so the changed part can be highlighted.
    param([string]$Old, [string]$New)
    $o = @($Old -split '\.')
    $n = @($New -split '\.')
    $i = 0
    while ($i -lt $o.Count -and $i -lt $n.Count -and $o[$i] -eq $n[$i]) { $i++ }
    if ($i -ge $n.Count) { return @('', $New) }
    $same = if ($i -gt 0) { ($n[0..($i - 1)] -join '.') + '.' } else { '' }
    $diff = $n[$i..($n.Count - 1)] -join '.'
    return @($same, $diff)
}

function Test-RecentAutoAttempt {
    param([string]$Key)
    $t = [string]$script:Config.AutoAttempts[$Key]
    if (-not $t) { return $false }
    $d = [datetime]::MinValue
    if ([datetime]::TryParse($t, $script:Inv, [System.Globalization.DateTimeStyles]::None, [ref]$d)) {
        return (((Get-Date) - $d).TotalHours -lt 12)
    }
    $false
}

$script:SuggestWords = @('ngenuity', 'hyperx', 'adobe', 'g hub', 'ghub', 'logitech', 'nvidia', 'winrar',
                         'battle.net', 'battlenet', 'steam', 'nzxt')
function Test-Suggested {
    param($Pkg)
    $hay = ('{0} {1}' -f $Pkg.Name, $Pkg.Id).ToLowerInvariant()
    foreach ($w in $script:SuggestWords) { if ($hay.Contains($w)) { return $true } }
    $false
}

# ---------------------------------------------------------------- icon (drawn in code, no files needed)
function New-AppBitmap {
    param([int]$Size)
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $Size / 64.0
    $r = [float](15 * $s)
    $w = [float]($Size - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc([float]0, [float]0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($w - 2 * $r, [float]0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($w - 2 * $r, $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc([float]0, $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 242, 169, 59))
    $g.FillPath($bg, $path)
    $ink = [System.Drawing.Color]::FromArgb(255, 26, 29, 35)
    $pen = New-Object System.Drawing.Pen($ink, [float](7 * $s))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($pen, [float](32 * $s), [float](13 * $s), [float](32 * $s), [float](32 * $s))
    $head = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([float](19 * $s), [float](28 * $s)),
        [System.Drawing.PointF]::new([float](45 * $s), [float](28 * $s)),
        [System.Drawing.PointF]::new([float](32 * $s), [float](43 * $s))
    )
    $inkBrush = New-Object System.Drawing.SolidBrush($ink)
    $g.FillPolygon($inkBrush, $head)
    $g.DrawLine($pen, [float](19 * $s), [float](51 * $s), [float](45 * $s), [float](51 * $s))
    $g.Dispose(); $pen.Dispose(); $bg.Dispose(); $inkBrush.Dispose(); $path.Dispose()
    $bmp
}

function ConvertTo-ImageSource {
    param($Bitmap)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $ms.Position = 0
    $img = New-Object System.Windows.Media.Imaging.BitmapImage
    $img.BeginInit()
    $img.CacheOption = [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad
    $img.StreamSource = $ms
    $img.EndInit()
    $img.Freeze()
    $img
}

function Save-IcoFile {
    # Writes a PNG-compressed .ico (used for the desktop/Start menu shortcut).
    param($Bitmap, [string]$Path)
    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $png = $ms.ToArray()
    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $dim = if ($Bitmap.Width -ge 256) { 0 } else { $Bitmap.Width }
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$png.Length); $bw.Write([uint32]22)
    $bw.Write($png)
    $bw.Close()
}

function Set-DarkTitleBar {
    param($Win)
    try {
        $h = (New-Object System.Windows.Interop.WindowInteropHelper($Win)).EnsureHandle()
        $on = 1
        [void][UpdTracker.Native]::DwmSetWindowAttribute($h, 20, [ref]$on, 4)
        $caption = 0x00231D1A   # COLORREF for #1A1D23
        [void][UpdTracker.Native]::DwmSetWindowAttribute($h, 35, [ref]$caption, 4)
    } catch { }
}

# ================================================================ state
$script:Config         = Read-Config
$script:Sync           = [hashtable]::Synchronized(@{ Status = ''; Current = $null })
$script:Job            = $null
$script:Runspace       = $null
$script:AllPackages    = $null
$script:PackageIndex   = @{}
$script:InstallDates   = @{}
$script:LastErrors     = @{}
$script:UpdateQueue    = @()
$script:LastCurrent    = $null
$script:LastCheckError = $null
$script:NextCheck      = $null
$script:OpenPickerAfterCheck = $false
$script:Exiting        = $false
$script:TrayHintShown  = $false
$script:IsAdmin        = Test-IsAdmin
$script:Colors = @{
    green = '#7FB59A'; amber = '#F2A93B'; blue = '#7FA7E0'; red = '#E5707A'; gray = '#8F96A3'
}

# ================================================================ UI behaviour
function Set-Status {
    param([string]$Text, [string]$Tone = 'gray')
    $script:UI.StatusText.Text = $Text
    $script:UI.StatusText.Foreground = Get-Brush $script:Colors[$Tone]
}

function Set-IdleStatus {
    if ($script:LastCheckError) {
        Set-Status ('The last check failed: ' + $script:LastCheckError + ' It will try again in 15 minutes.') 'red'
    } elseif ($script:IsAdmin) {
        Set-Status 'Ready. Running with admin rights, so updates install without permission prompts.'
    } else {
        Set-Status 'Ready. Some installers will ask for admin permission. Turn on Start with Windows for silent updates.'
    }
}

function Show-Balloon {
    param([string]$Title, [string]$Text, [string]$Kind = 'Info')
    if (-not $script:Config.Notify -or -not $script:Tray) { return }
    if ($Text.Length -gt 250) { $Text = $Text.Substring(0, 247) + '...' }
    try { $script:Tray.ShowBalloonTip(8000, $Title, $Text, [System.Windows.Forms.ToolTipIcon]$Kind) } catch { }
}

function Show-MainWindow {
    $w = $script:Window
    if (-not $w.IsVisible) { $w.Show() }
    if ($w.WindowState -eq [System.Windows.WindowState]::Minimized) { $w.WindowState = [System.Windows.WindowState]::Normal }
    [void]$w.Activate()
    $w.Topmost = $true
    $w.Topmost = $false
}

function Set-Busy {
    param([bool]$On)
    $script:UI.Busy.Visibility = if ($On) { 'Visible' } else { 'Hidden' }
    $script:UI.BtnCheck.IsEnabled = -not $On
    $script:UI.BtnAdd.IsEnabled = -not $On
    if ($script:MiCheck) { $script:MiCheck.Enabled = -not $On }
    if ($script:MiAll) { $script:MiAll.Enabled = -not $On }
    if ($On) { $script:UI.StatusText.Foreground = Get-Brush $script:Colors.gray } else { Set-IdleStatus }
    Update-View
}

function Update-View {
    if (-not $script:UI) { return }
    $busy = [bool]$script:Job
    $updating = $busy -and $script:Job.Kind -eq 'update'
    $rows = New-Object System.Collections.ArrayList
    $updCount = 0
    foreach ($app in $script:Config.Apps) {
        $id = [string]$app.Id
        $p = $script:PackageIndex[$id]
        $name = if ($p -and $p.Name) { $p.Name } elseif ($app.Name) { $app.Name } else { $id }
        $installed = if ($p -and $p.Version) { $p.Version } else { $script:Dash }

        $instSub = ''
        $lu = [string]$script:Config.LastUpdated[$id]
        $idt = [string]$script:InstallDates[$id]
        if ($lu) { $instSub = 'updated ' + (Format-Day $lu) } elseif ($idt) { $instSub = 'installed ' + (Format-Day $idt) }

        $hasUpd = [bool]($p -and $p.Available -and $p.Version -ne 'Unknown')
        $key = ''; $same = ''; $diff = ''; $availSub = ''
        if ($hasUpd) {
            $updCount++
            $key = $id + '|' + $p.Available
            $parts = @(Split-VersionDiff $p.Version $p.Available)
            $same = [string]$parts[0]; $diff = [string]$parts[1]
            $lines = @()
            $rd = [string]$script:Config.ReleaseDates[$key]
            if ($rd) { $lines += 'released ' + (Format-Day $rd) }
            $dd = [string]$script:Config.Detected[$key]
            if ($dd) { $lines += 'found ' + (Format-Day $dd) }
            $availSub = $lines -join "`n"
        }

        $status = 'Up to date'; $tone = 'green'; $sub = ''; $tip = $null; $rank = 3
        if ($updating -and $script:Sync.Current -eq $id) {
            $status = 'Installing'; $tone = 'blue'; $sub = 'this can take a minute'; $rank = 0
        } elseif ($updating -and (@($script:UpdateQueue) -contains $id)) {
            $status = 'Waiting'; $tone = 'blue'; $sub = 'next in line'; $rank = 0
        } elseif (-not $p) {
            if ($null -eq $script:AllPackages) {
                $status = if ($busy) { 'Checking' } else { 'Not checked yet' }; $tone = 'gray'
            } else {
                $status = 'Not found'; $tone = 'gray'; $sub = 'uninstalled?'
                $tip = 'winget no longer sees this app. It may have been uninstalled.'
            }
            $rank = 4
        } elseif ($p.Version -eq 'Unknown') {
            $status = 'Version unknown'; $tone = 'gray'; $sub = 'updates itself when opened'; $rank = 4
            $tip = "winget can't read this app's version. Launchers like this one update themselves when you open them."
        } elseif ($hasUpd) {
            if ($script:LastErrors[$key]) {
                $status = 'Update failed'; $tone = 'red'; $sub = $script:LastErrors[$key]; $tip = $script:LastErrors[$key]; $rank = 1
            } else {
                $status = 'Update available'; $tone = 'amber'; $rank = 2
                if ($app.Auto) { $sub = 'installs automatically' }
            }
        }

        $edgeVis   = if ($hasUpd) { 'Visible' } else { 'Hidden' }
        $arrowText = if ($hasUpd) { $script:Arrow + '  ' } else { '' }
        $updVis    = if ($hasUpd) { 'Visible' } else { 'Collapsed' }
        $updLabel  = if ($tone -eq 'red') { 'Retry' } else { 'Update' }
        $canUpdate = $hasUpd -and -not $busy
        [void]$rows.Add([pscustomobject]@{
            Id = $id; Name = $name; Installed = $installed; InstalledSub = $instSub
            ArrowText = $arrowText; AvailSame = $same; AvailDiff = $diff; AvailableSub = $availSub; EdgeVis = $edgeVis
            Status = $status; StatusColor = $script:Colors[$tone]; StatusSub = $sub; StatusTip = $tip
            Auto = [bool]$app.Auto; UpdateVis = $updVis; UpdateLabel = $updLabel; CanUpdate = $canUpdate; Rank = $rank
        })
    }
    $sorted = @($rows | Sort-Object -Property @{ Expression = { $_.Rank } }, @{ Expression = { $_.Name } })
    $script:UI.List.ItemsSource = $null
    $script:UI.List.ItemsSource = $sorted
    $script:UI.Empty.Visibility = if ($rows.Count) { 'Collapsed' } else { 'Visible' }

    $script:UI.BtnUpdateAll.Content = if ($updCount) { "Update all ($updCount)" } else { 'Update all' }
    $script:UI.BtnUpdateAll.IsEnabled = ($updCount -gt 0) -and -not $busy

    $lc = Get-LastCheckTime
    $sentences = @()
    if ($lc) { $sentences += ('Last checked ' + (Format-When $lc) + '.') } else { $sentences += 'Not checked yet.' }
    if ($lc -and $script:NextCheck -and -not $busy) { $sentences += ('Next check ' + (Format-When $script:NextCheck) + '.') }
    if ($updCount -eq 1) { $sentences += '1 update is ready.' }
    elseif ($updCount -gt 1) { $sentences += "$updCount updates are ready." }
    elseif ($lc -and $rows.Count) { $sentences += 'Everything is up to date.' }
    $script:UI.SubTitle.Text = $sentences -join ' '

    if ($script:Tray) {
        $script:Tray.Text = if ($updCount -eq 1) { 'Update Tracker: 1 update ready' }
                            elseif ($updCount) { "Update Tracker: $updCount updates ready" }
                            else { 'Update Tracker' }
    }
}

# ---------------------------------------------------------------- background worker plumbing
function New-WorkerRunspace {
    $iss = [System.Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    try { $iss.ExecutionPolicy = [Microsoft.PowerShell.ExecutionPolicy]::Bypass } catch { }
    foreach ($fname in $script:WorkerFunctionNames) {
        $def = (Get-Command -Name $fname -CommandType Function).Definition
        $entry = New-Object System.Management.Automation.Runspaces.SessionStateFunctionEntry -ArgumentList $fname, $def
        $iss.Commands.Add($entry)
    }
    $rs = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($iss)
    $rs.ApartmentState = [System.Threading.ApartmentState]::STA
    $rs.ThreadOptions = [System.Management.Automation.Runspaces.PSThreadOptions]::ReuseThread
    $rs.Open()
    $rs
}

function Start-Worker {
    param([string]$Kind, [scriptblock]$Script, [object[]]$Arguments, [scriptblock]$OnDone)
    if ($script:Job) { return $false }
    if (-not $script:Runspace -or $script:Runspace.RunspaceStateInfo.State -ne 'Opened') {
        $script:Runspace = New-WorkerRunspace
    }
    $ps = [System.Management.Automation.PowerShell]::Create()
    $ps.Runspace = $script:Runspace
    [void]$ps.AddScript($Script.ToString())
    foreach ($a in $Arguments) { [void]$ps.AddArgument($a) }
    $script:Job = @{ Kind = $Kind; PS = $ps; Handle = $ps.BeginInvoke(); OnDone = $OnDone }
    Set-Busy $true
    $true
}

# ---------------------------------------------------------------- checking
function Start-Check {
    if ($script:Job) { return }
    [string[]]$ids = @($script:Config.Apps | ForEach-Object { [string]$_.Id })
    $script:Sync.Status = 'Starting the check...'
    [void](Start-Worker -Kind 'check' -Script $script:CheckJob -Arguments @($ids, $script:Config.ReleaseDates, $script:Sync) `
        -OnDone { param($r, $e) Complete-Check $r $e })
}

function Complete-Check {
    param($Result, $ErrorText)
    if ($ErrorText -or -not $Result -or -not $Result.Ok) {
        $msg = if ($ErrorText) { $ErrorText } elseif ($Result) { [string]$Result.Error } else { 'winget returned nothing.' }
        $script:LastCheckError = $msg
        $script:OpenPickerAfterCheck = $false
        $script:NextCheck = (Get-Date).AddMinutes(15)
        Write-Log ('CHECK FAILED  ' + $msg)
        Set-IdleStatus
        Update-View
        return
    }
    $script:LastCheckError = $null
    $script:AllPackages = @($Result.All)
    $index = @{}
    foreach ($p in $script:AllPackages) { if ($p.Id -and -not $index.ContainsKey($p.Id)) { $index[$p.Id] = $p } }
    $script:PackageIndex = $index
    foreach ($k in @($Result.Dates.Keys)) { $script:Config.ReleaseDates[$k] = [string]$Result.Dates[$k] }
    $script:InstallDates = @{}
    foreach ($k in @($Result.InstallDates.Keys)) { $script:InstallDates[$k] = [string]$Result.InstallDates[$k] }

    $now = Get-Date
    $today = $now.ToString('yyyy-MM-dd')
    $script:Config.LastCheck = $now.ToString('yyyy-MM-dd HH:mm:ss')
    $script:NextCheck = $now.AddHours($script:Config.IntervalHours)

    $current = @{}
    $newOnes = New-Object System.Collections.ArrayList
    $autoIds = New-Object System.Collections.ArrayList
    foreach ($app in $script:Config.Apps) {
        $p = $index[$app.Id]
        if (-not ($p -and $p.Available -and $p.Version -ne 'Unknown')) { continue }
        $key = $app.Id + '|' + $p.Available
        $current[$key] = $true
        if (-not $script:Config.Detected.ContainsKey($key)) { $script:Config.Detected[$key] = $today }
        if ($app.Auto) {
            if (-not (Test-RecentAutoAttempt $key)) { [void]$autoIds.Add([string]$app.Id) }
        } elseif (-not $script:Config.Notified.ContainsKey($key)) {
            $script:Config.Notified[$key] = $today
            [void]$newOnes.Add(('{0}  {1} {2} {3}' -f $p.Name, $p.Version, $script:Arrow, $p.Available))
        }
    }
    # Forget bookkeeping for versions that are no longer pending.
    foreach ($dictName in 'Detected', 'Notified', 'AutoAttempts', 'ReleaseDates') {
        $d = $script:Config[$dictName]
        foreach ($k in @($d.Keys)) { if (-not $current.ContainsKey($k)) { $d.Remove($k) } }
    }
    Save-Config
    Write-Log ('CHECK OK  {0} tracked, {1} with updates' -f $script:Config.Apps.Count, $current.Count)
    Set-IdleStatus
    Update-View

    if ($newOnes.Count -and -not ($script:Window.IsVisible -and $script:Window.IsActive)) {
        $title = if ($newOnes.Count -eq 1) { '1 update is ready' } else { '{0} updates are ready' -f $newOnes.Count }
        Show-Balloon $title ($newOnes -join "`n")
    }

    if ($script:OpenPickerAfterCheck -or -not $script:Config.SetupDone) {
        $script:OpenPickerAfterCheck = $false
        [void]$script:Window.Dispatcher.BeginInvoke([System.Action]{ Show-Picker })
        return
    }
    if ($autoIds.Count) { Start-Update -Ids ([string[]]$autoIds.ToArray()) -Auto }
}

# ---------------------------------------------------------------- updating
function Start-Update {
    param([string[]]$Ids, [switch]$Auto)
    if ($script:Job) { Set-Status 'Busy with another task. Try again in a moment.'; return }
    $items = @()
    foreach ($id in $Ids) {
        $p = $script:PackageIndex[$id]
        if (-not ($p -and $p.Available -and $p.Version -ne 'Unknown')) { continue }
        $items += [pscustomobject]@{ Id = $p.Id; Name = $p.Name; Source = $p.Source; From = $p.Version; To = $p.Available }
        if ($Auto) { $script:Config.AutoAttempts[$p.Id + '|' + $p.Available] = (Get-Date).ToString('s') }
    }
    if (-not $items.Count) { return }
    if ($Auto) { Save-Config }
    $script:UpdateQueue = @($items | ForEach-Object { $_.Id })
    $script:Sync.Current = $null
    $how = if ($Auto) { 'auto' } else { 'manual' }
    Write-Log ('START {0} update: {1}' -f $how, (($items | ForEach-Object { $_.Name }) -join ', '))
    [void](Start-Worker -Kind 'update' -Script $script:UpdateJob -Arguments @($items, $script:Sync) `
        -OnDone { param($r, $e) Complete-Update $r $e })
}

function Start-UpdateAll {
    $ids = @()
    foreach ($a in $script:Config.Apps) {
        $p = $script:PackageIndex[$a.Id]
        if ($p -and $p.Available -and $p.Version -ne 'Unknown') { $ids += [string]$a.Id }
    }
    if ($ids.Count) { Start-Update -Ids $ids }
}

function Get-Tail {
    param([string]$Text, [int]$Lines = 25)
    $l = @($Text -split "[\r\n]+" | Where-Object { $_.Trim() -and $_ -match '[A-Za-z0-9]{2}' })
    (@($l | Select-Object -Last $Lines) | ForEach-Object { '      ' + $_.Trim() }) -join [Environment]::NewLine
}

function Complete-Update {
    param($Result, $ErrorText)
    $script:UpdateQueue = @()
    if ($ErrorText -or -not $Result) {
        Write-Log ('UPDATE RUN FAILED  ' + $ErrorText)
        Set-Status ('The update run stopped: ' + $ErrorText) 'red'
        Start-Check
        return
    }
    $done = @(); $failed = @()
    foreach ($r in @($Result.Results)) {
        $key = $r.Id + '|' + $r.To
        if ($r.Ok) {
            $script:Config.LastUpdated[$r.Id] = (Get-Date).ToString('yyyy-MM-dd')
            $script:LastErrors.Remove($key)
            $done += ('{0}  {1} {2} {3}' -f $r.Name, $r.From, $script:Arrow, $r.To)
            Write-Log ('UPDATED  {0}  {1} -> {2}  ({3})' -f $r.Name, $r.From, $r.To, $r.Message)
        } else {
            $script:LastErrors[$key] = $r.Message
            $failed += ('{0}: {1}' -f $r.Name, $r.Message)
            Write-Log ('FAILED   {0}  {1} -> {2}  {3}' -f $r.Name, $r.From, $r.To, $r.Message)
            $tail = Get-Tail $r.Output 25
            if ($tail) { Write-Log ("winget output:`r`n" + $tail) }
        }
    }
    Save-Config
    $lines = @($done) + @($failed)
    if ($lines.Count) {
        $title = if (-not $failed.Count) { 'Updates installed' } elseif (-not $done.Count) { 'Update failed' } else { 'Some updates failed' }
        $kind = if ($failed.Count) { 'Warning' } else { 'Info' }
        Show-Balloon $title ($lines -join "`n") $kind
    }
    Start-Check   # re-read versions so the list shows what is really installed now
}

# ---------------------------------------------------------------- app list changes
function Get-TrackedApp {
    param([string]$Id)
    foreach ($a in $script:Config.Apps) { if ($a.Id -eq $Id) { return $a } }
    $null
}

function Set-AppAuto {
    param([string]$Id, [bool]$On)
    $a = Get-TrackedApp $Id
    if (-not $a) { return }
    $a.Auto = $On
    Save-Config
    $p = $script:PackageIndex[$Id]
    if ($On -and $p -and $p.Available -and $p.Version -ne 'Unknown' -and -not $script:Job) {
        Start-Update -Ids @($Id) -Auto      # an update is already waiting: install it now
    } else {
        Update-View
    }
}

function Remove-App {
    param([string]$Id)
    $a = Get-TrackedApp $Id
    if ($a) { $script:Config.Apps.Remove($a); Save-Config; Update-View }
}

function Update-PickerCount {
    $n = 0
    foreach ($cb in $script:PickerBoxes) { if ($cb.IsChecked) { $n++ } }
    $script:PickerUI.Count.Text = if ($n -eq 1) { '1 app selected' } else { "$n apps selected" }
}

function New-PickerItem {
    param($Pkg, [bool]$Checked)
    $cb = New-Object System.Windows.Controls.CheckBox
    $cb.Style = $script:PickerWin.FindResource('Check')
    $cb.Tag = $Pkg
    $cb.IsChecked = $Checked
    $panel = New-Object System.Windows.Controls.StackPanel
    $t1 = New-Object System.Windows.Controls.TextBlock
    $t1.Text = $Pkg.Name
    $t1.FontSize = 13.5
    $t1.FontWeight = [System.Windows.FontWeights]::SemiBold
    $t1.TextTrimming = [System.Windows.TextTrimming]::CharacterEllipsis
    $t2 = New-Object System.Windows.Controls.TextBlock
    $t2.FontSize = 12
    $t2.Margin = New-Object System.Windows.Thickness(0, 3, 0, 0)
    $t2.Foreground = Get-Brush '#8F96A3'
    $t2.TextTrimming = [System.Windows.TextTrimming]::CharacterEllipsis
    $via = if ($Pkg.Source -eq 'msstore') { 'Microsoft Store' } else { 'winget' }
    $t2.Text = 'Version {0}      {1}      via {2}' -f $Pkg.Version, $Pkg.Id, $via
    if ($Pkg.Available) {
        $run = New-Object System.Windows.Documents.Run(('      {0} is available' -f $Pkg.Available))
        $run.Foreground = Get-Brush '#F2A93B'
        $t2.Inlines.Add($run)
    }
    [void]$panel.Children.Add($t1)
    [void]$panel.Children.Add($t2)
    $cb.Content = $panel
    $cb.Add_Click({ Update-PickerCount })
    $cb
}

function Show-Picker {
    if ($null -eq $script:AllPackages) { return }
    $script:PickerWin = [Windows.Markup.XamlReader]::Parse($script:PickerXaml.Replace('%STYLES%', $script:StylesXaml))
    $script:PickerUI = @{}
    foreach ($n in 'Items', 'Search', 'Hint', 'Count', 'Intro', 'BtnOk', 'BtnCancel') {
        $script:PickerUI[$n] = $script:PickerWin.FindName($n)
    }
    $first = -not $script:Config.SetupDone
    $script:PickerUI.Intro.Text = if ($first) {
        'These are the installed apps that winget can update. The ones that look like yours (G HUB, NZXT CAM, Steam, WinRAR, Adobe, NVIDIA and so on) are already ticked. Untick anything you do not care about. You can change this later with Add apps.'
    } else {
        'Tick the apps you want to keep up to date. Only apps that winget can update are listed.'
    }

    $tracked = @{}
    foreach ($a in $script:Config.Apps) { $tracked[$a.Id] = $true }
    $seen = @{}
    $entries = New-Object System.Collections.ArrayList
    foreach ($pkg in $script:AllPackages) {
        if (-not $pkg.Source -or $seen.ContainsKey($pkg.Id)) { continue }
        $seen[$pkg.Id] = $true
        $pre = $tracked.ContainsKey($pkg.Id) -or ($first -and (Test-Suggested $pkg))
        [void]$entries.Add([pscustomobject]@{ Pkg = $pkg; Pre = $pre })
    }
    $entries = @($entries | Sort-Object -Property @{ Expression = { -not $_.Pre } }, @{ Expression = { $_.Pkg.Name } })

    $script:PickerBoxes = New-Object System.Collections.ArrayList
    foreach ($e in $entries) {
        $cb = New-PickerItem -Pkg $e.Pkg -Checked ([bool]$e.Pre)
        [void]$script:PickerUI.Items.Children.Add($cb)
        [void]$script:PickerBoxes.Add($cb)
    }
    if (-not $entries.Count) {
        $empty = New-Object System.Windows.Controls.TextBlock
        $empty.Text = 'winget did not report any installed apps it can update.'
        $empty.Margin = New-Object System.Windows.Thickness(12)
        $empty.Foreground = Get-Brush '#8F96A3'
        [void]$script:PickerUI.Items.Children.Add($empty)
    }
    Update-PickerCount

    $script:PickerUI.Search.Add_TextChanged({
        $q = $script:PickerUI.Search.Text.Trim().ToLowerInvariant()
        $script:PickerUI.Hint.Visibility = if ($q) { 'Hidden' } else { 'Visible' }
        foreach ($cb in $script:PickerBoxes) {
            $pkg = $cb.Tag
            $hit = (-not $q) -or $pkg.Name.ToLowerInvariant().Contains($q) -or $pkg.Id.ToLowerInvariant().Contains($q)
            $cb.Visibility = if ($hit) { 'Visible' } else { 'Collapsed' }
        }
    })
    $script:PickerUI.BtnOk.Add_Click({ $script:PickerWin.DialogResult = $true })

    if ($script:Window.IsVisible) { $script:PickerWin.Owner = $script:Window }
    else { $script:PickerWin.WindowStartupLocation = [System.Windows.WindowStartupLocation]::CenterScreen }
    $script:PickerWin.Icon = $script:Window.Icon
    Set-DarkTitleBar $script:PickerWin
    [void]$script:PickerUI.Search.Focus()

    $ok = $script:PickerWin.ShowDialog()
    $changed = $false
    if ($ok) {
        $old = @{}
        foreach ($a in $script:Config.Apps) { $old[$a.Id] = $a }
        $newApps = New-Object System.Collections.ArrayList
        foreach ($cb in $script:PickerBoxes) {
            if (-not $cb.IsChecked) { continue }
            $pkg = $cb.Tag
            $auto = if ($old.ContainsKey($pkg.Id)) { [bool]$old[$pkg.Id].Auto } else { $false }
            [void]$newApps.Add(@{ Id = [string]$pkg.Id; Name = [string]$pkg.Name; Auto = $auto })
            if (-not $old.ContainsKey($pkg.Id)) { $changed = $true }
        }
        if ($newApps.Count -ne $script:Config.Apps.Count) { $changed = $true }
        $script:Config.Apps = $newApps
    }
    $script:Config.SetupDone = $true
    Save-Config
    Update-View
    if ($changed) { Start-Check }   # fetch release and install dates for newly added apps
}

# ---------------------------------------------------------------- settings actions
function Set-Startup {
    param([bool]$Enable)
    try {
        if ($script:IsAdmin) {
            if ($Enable) { Register-StartupTask -User (Get-CurrentUserName) }
            else { Unregister-ScheduledTask -TaskName $script:TaskName -Confirm:$false -ErrorAction Stop }
        } else {
            $mode = if ($Enable) { '-RegisterStartup' } else { '-UnregisterStartup' }
            $argLine = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" {1} -ForUser "{2}"' -f $script:ScriptPath, $mode, (Get-CurrentUserName)
            Start-Process -FilePath $script:PsExe -ArgumentList $argLine -Verb RunAs -WindowStyle Hidden -Wait
        }
    } catch {
        Set-Status ('The startup setting was not changed: ' + $_.Exception.Message) 'red'
    }
    $on = Test-StartupTask
    $script:UI.ChkStartup.IsChecked = $on
    if ($Enable -and $on) { Set-Status 'Update Tracker will start in the tray, with admin rights, the next time you sign in.' 'green' }
    elseif (-not $Enable -and -not $on) { Set-Status 'Update Tracker will no longer start with Windows.' }
}

function New-Shortcuts {
    try {
        $shell = New-Object -ComObject WScript.Shell
        foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
            $lnk = $shell.CreateShortcut((Join-Path $dir 'Update Tracker.lnk'))
            $lnk.TargetPath = $script:PsExe
            $lnk.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $script:ScriptPath
            $lnk.WorkingDirectory = Split-Path -Parent $script:ScriptPath
            if (Test-Path -LiteralPath $script:IconPath) { $lnk.IconLocation = $script:IconPath + ',0' }
            $lnk.WindowStyle = 7
            $lnk.Description = 'Check your apps for updates'
            $lnk.Save()
        }
        Set-Status 'Shortcuts added to your desktop and Start menu.' 'green'
    } catch {
        Set-Status ('Shortcuts were not created: ' + $_.Exception.Message) 'red'
    }
}

function Exit-App {
    if ($script:Job -and $script:Job.Kind -eq 'update') {
        $answer = [System.Windows.MessageBox]::Show('An update is still installing. Exit anyway? The installer will keep running on its own.',
            $script:AppTitle, [System.Windows.MessageBoxButton]::YesNo, [System.Windows.MessageBoxImage]::Warning)
        if ($answer -ne [System.Windows.MessageBoxResult]::Yes) { return }
    }
    $script:Exiting = $true
    try { $script:PollTimer.Stop(); $script:SchedTimer.Stop() } catch { }
    try { $script:Tray.Visible = $false; $script:Tray.Dispose() } catch { }
    try { $script:App.Shutdown() } catch { }
}

# ================================================================ build the window
$script:Window = [Windows.Markup.XamlReader]::Parse($script:MainXaml.Replace('%STYLES%', $script:StylesXaml))
$script:UI = @{}
foreach ($n in 'List', 'Empty', 'SubTitle', 'StatusText', 'Busy', 'BtnAdd', 'BtnCheck', 'BtnUpdateAll',
               'ChkStartup', 'ChkTray', 'ChkNotify', 'BtnLog', 'BtnShortcut', 'Logo',
               'Int1', 'Int3', 'Int6', 'Int12', 'Int24') {
    $script:UI[$n] = $script:Window.FindName($n)
}

$bmp64 = New-AppBitmap 64
$script:UI.Logo.Source = ConvertTo-ImageSource $bmp64
$script:Window.Icon = ConvertTo-ImageSource $bmp64
try { if (-not (Test-Path -LiteralPath $script:IconPath)) { Save-IcoFile (New-AppBitmap 256) $script:IconPath } } catch { }
Set-DarkTitleBar $script:Window

# Buttons inside the rows (Update / Auto / remove) all bubble up to one handler.
$script:UI.List.AddHandler([System.Windows.Controls.Primitives.ButtonBase]::ClickEvent, [System.Windows.RoutedEventHandler]{
    param($sender, $e)
    $src = $e.OriginalSource
    if ($src -isnot [System.Windows.Controls.Primitives.ButtonBase]) { return }
    $row = $src.DataContext
    if (-not $row) { return }
    switch ([string]$src.Tag) {
        'update' { Start-Update -Ids @([string]$row.Id) }
        'auto'   { Set-AppAuto -Id ([string]$row.Id) -On ([bool]$src.IsChecked) }
        'remove' { Remove-App -Id ([string]$row.Id) }
    }
})

$script:UI.BtnCheck.Add_Click({ Start-Check })
$script:UI.BtnUpdateAll.Add_Click({ Start-UpdateAll })
$script:UI.BtnAdd.Add_Click({
    if ($null -ne $script:AllPackages) { Show-Picker }
    else { $script:OpenPickerAfterCheck = $true; Start-Check }
})
$script:UI.BtnLog.Add_Click({
    if (-not (Test-Path -LiteralPath $script:LogPath)) { Write-Log 'Log started.' }
    Start-Process -FilePath 'notepad.exe' -ArgumentList ('"{0}"' -f $script:LogPath)
})
$script:UI.BtnShortcut.Add_Click({ New-Shortcuts })

foreach ($h in 1, 3, 6, 12, 24) {
    $rb = $script:UI["Int$h"]
    if ($h -eq $script:Config.IntervalHours) { $rb.IsChecked = $true }
    $rb.Add_Checked({
        param($s, $e)
        $script:Config.IntervalHours = [int]$s.Tag
        Save-Config
        $lc = Get-LastCheckTime
        if ($lc) { $script:NextCheck = $lc.AddHours($script:Config.IntervalHours) }
        Update-View
    })
}

$script:UI.ChkStartup.IsChecked = Test-StartupTask
$script:UI.ChkStartup.Add_Click({ Set-Startup -Enable ([bool]$script:UI.ChkStartup.IsChecked) })
$script:UI.ChkTray.IsChecked = $script:Config.KeepInTray
$script:UI.ChkTray.Add_Click({ $script:Config.KeepInTray = [bool]$script:UI.ChkTray.IsChecked; Save-Config })
$script:UI.ChkNotify.IsChecked = $script:Config.Notify
$script:UI.ChkNotify.Add_Click({ $script:Config.Notify = [bool]$script:UI.ChkNotify.IsChecked; Save-Config })

$script:Window.Add_Closing({
    param($s, $e)
    if ($script:Exiting) { return }
    $e.Cancel = $true
    if ($script:Config.KeepInTray) {
        $script:Window.Hide()
        if (-not $script:TrayHintShown) {
            $script:TrayHintShown = $true
            Show-Balloon 'Update Tracker is still running' 'It keeps checking in the background. Right-click the tray icon to exit.'
        }
    } else {
        [void]$script:Window.Dispatcher.BeginInvoke([System.Action]{ Exit-App })
    }
})

# ================================================================ tray icon
$script:Tray = New-Object System.Windows.Forms.NotifyIcon
$script:Tray.Icon = [System.Drawing.Icon]::FromHandle((New-AppBitmap 32).GetHicon())
$script:Tray.Text = 'Update Tracker'
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miOpen = New-Object System.Windows.Forms.ToolStripMenuItem('Open Update Tracker')
$miOpen.Font = New-Object System.Drawing.Font($miOpen.Font, [System.Drawing.FontStyle]::Bold)
$miOpen.Add_Click({ Show-MainWindow })
$script:MiCheck = New-Object System.Windows.Forms.ToolStripMenuItem('Check now')
$script:MiCheck.Add_Click({ Start-Check })
$script:MiAll = New-Object System.Windows.Forms.ToolStripMenuItem('Update all')
$script:MiAll.Add_Click({ Start-UpdateAll })
$miExit = New-Object System.Windows.Forms.ToolStripMenuItem('Exit')
$miExit.Add_Click({ Exit-App })
[void]$menu.Items.Add($miOpen)
[void]$menu.Items.Add($script:MiCheck)
[void]$menu.Items.Add($script:MiAll)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($miExit)
$script:Tray.ContextMenuStrip = $menu
$script:Tray.Add_MouseClick({ param($s, $e) if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { Show-MainWindow } })
$script:Tray.Add_BalloonTipClicked({ Show-MainWindow })
$script:Tray.Visible = $true

# ================================================================ timers
# Poll: finishes background jobs, shows progress, answers "show window" requests from a second launch.
$script:PollTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:PollTimer.Interval = [TimeSpan]::FromMilliseconds(300)
$script:PollTimer.Add_Tick({
    try {
        if (Test-Path -LiteralPath $script:ShowFlag) {
            Remove-Item -LiteralPath $script:ShowFlag -Force -ErrorAction SilentlyContinue
            Show-MainWindow
        }
        $j = $script:Job
        if ($null -eq $j) { return }
        $st = [string]$script:Sync.Status
        if ($st -and $script:UI.StatusText.Text -ne $st) { $script:UI.StatusText.Text = $st }
        if ($j.Kind -eq 'update' -and $script:Sync.Current -ne $script:LastCurrent) {
            $script:LastCurrent = $script:Sync.Current
            Update-View
        }
        if (-not $j.Handle.IsCompleted) { return }
        $res = $null; $err = $null
        try {
            $out = $j.PS.EndInvoke($j.Handle)
            if ($out -and $out.Count) { $res = $out[$out.Count - 1] }
        } catch {
            $err = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        }
        try { $j.PS.Dispose() } catch { }
        $script:Job = $null
        $script:LastCurrent = $null
        Set-Busy $false
        & $j.OnDone $res $err
    } catch {
        Write-Log ('Background task error: ' + $_.Exception.Message)
    }
})

# Schedule: runs the periodic check (auto-updates follow each check).
$script:SchedTimer = New-Object System.Windows.Threading.DispatcherTimer
$script:SchedTimer.Interval = [TimeSpan]::FromSeconds(20)
$script:SchedTick = 0
$script:SchedTimer.Add_Tick({
    try {
        if (-not $script:Job -and $script:NextCheck -and (Get-Date) -ge $script:NextCheck) { Start-Check; return }
        $script:SchedTick++
        if ($script:SchedTick % 3 -eq 0 -and -not $script:Job -and $script:Window.IsVisible) { Update-View }
    } catch {
        Write-Log ('Scheduler error: ' + $_.Exception.Message)
    }
})

# ================================================================ go
Write-Log ('Started ({0}, {1}).' -f $(if ($Tray) { 'tray' } else { 'window' }), $(if ($script:IsAdmin) { 'admin' } else { 'standard' }))
Update-View
Set-IdleStatus
$script:PollTimer.Start()
$script:SchedTimer.Start()
if ($Tray) {
    $script:NextCheck = (Get-Date).AddSeconds(45)
} else {
    $script:Window.Show()
    Start-Check
}

[void]$script:App.Run()

try { if ($script:Runspace) { $script:Runspace.Dispose() } } catch { }
try { $script:Mutex.ReleaseMutex() } catch { }
[Environment]::Exit(0)

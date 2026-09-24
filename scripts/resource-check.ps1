param(
    [int]$Minutes = 30,
    [string]$ProcessName = 'SoftwareUpdateTracker',
    [double]$CpuBudgetPercent = 0.1,
    [double]$MemoryBudgetMB = 40
)
$ErrorActionPreference = 'Stop'
$proc = Get-Process -Name $ProcessName | Select-Object -First 1
$procId = $proc.Id
$cpuStart = $proc.TotalProcessorTime
$wallStart = Get-Date
$gpuPeak = 0.0
$deadline = $wallStart.AddMinutes($Minutes)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 15
    $engines = Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine | Where-Object { $_.Name -like "pid_${procId}_*" }
    $gpu = [double](($engines | Measure-Object -Property UtilizationPercentage -Sum).Sum)
    if ($gpu -gt $gpuPeak) { $gpuPeak = $gpu }
}
$proc.Refresh()
$wall = ((Get-Date) - $wallStart).TotalSeconds
$cpu = ($proc.TotalProcessorTime - $cpuStart).TotalSeconds / $wall / [Environment]::ProcessorCount * 100
$perf = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process | Where-Object { $_.IDProcess -eq $procId }
$memory = $perf.WorkingSetPrivate / 1MB
'CPU average : {0:N3}% (budget < {1}%)' -f $cpu, $CpuBudgetPercent
'Memory      : {0:N1} MB private working set (budget < {1} MB)' -f $memory, $MemoryBudgetMB
'GPU peak    : {0:N1}% (budget 0%)' -f $gpuPeak
if ($cpu -lt $CpuBudgetPercent -and $memory -lt $MemoryBudgetMB -and $gpuPeak -eq 0) { 'PASS'; exit 0 }
'FAIL'; exit 1

param(
    [Parameter(Mandatory)]
    [string]$App,
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot) 'test-results'),
    [switch]$Trace,
    [switch]$Desktop
)

$ErrorActionPreference = 'Stop'
$application = (Resolve-Path -LiteralPath $App).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Invoke-Check([string]$Name, [string]$Arguments) {
    $report = Join-Path $output "$Name.json"
    $quotedReport = '"' + $report + '"'
    $result = Start-Process -FilePath $application -ArgumentList ($Arguments.Replace('{report}', $quotedReport)) -Wait -PassThru
    if ($result.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)) {
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
        throw "GameDesk smoke test failed. Test=$Name Exit=$($result.ExitCode)"
    }
    if (!(Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).passed) {
        Get-Content -LiteralPath $report
        throw "GameDesk reported a failed smoke test. Test=$Name"
    }
    Get-Content -LiteralPath $report
}

Invoke-Check 'data' '--self-test-data {report}'
if ($Trace) {
    Invoke-Check 'ipv4' '--self-test {report} ipv4'
    Invoke-Check 'ipv6' '--self-test {report} ipv6'
    Invoke-Check 'collector' '--self-test-collector {report}'
}

function Write-DemoSession([string]$Path, [string]$Utc, [int]$Duration, [int]$Rtt, [int]$SpikeAt) {
    $writer = [IO.StreamWriter]::new($Path)
    $frequency = 10000000L
    $origin = 100 * $frequency
    try {
        $writer.WriteLine((@{type='start';version=1;qpc=$origin;frequency=$frequency;utc=$Utc} | ConvertTo-Json -Compress))
        for ($i = 0; $i -lt 4 * $Duration; $i++) {
            $qpc = $origin + $i * ($frequency / 4)
            $gap = if ($i -eq $SpikeAt * 4) { 93.0 } else { [math]::Round(18 + 2 * [math]::Sin($i * 0.38),1) }
            $spike = if ($gap -gt 80) {@{Qpc=$qpc;Ms=$gap}} else {$null}
            $writer.WriteLine((@{type='udp-gap';Qpc=$qpc;ms=$gap} | ConvertTo-Json -Compress))
            $probeTime = $origin + [math]::Floor($i / 8) * 2 * $frequency
            $probe = @{
                Match = @{Qpc=$probeTime; Ms=$Rtt + [int][math]::Floor($i / 8) % 4; Status='Success'}
                Gateway = @{Qpc=$probeTime; Ms=1 + [int][math]::Floor($i / 8) % 2; Status='Success'}
            }
            $sample = @{Qpc=$qpc;Status='stable';BaselineMs=18;Spike=$spike;Probes=$probe;EventsLost=0}
            $writer.WriteLine((@{type='sample';value=$sample} | ConvertTo-Json -Depth 6 -Compress))
            if ($i -eq $SpikeAt * 4 + 2) {
                $writer.WriteLine((@{type='felt-lag';qpc=$qpc} | ConvertTo-Json -Compress))
            }
        }
    }
    finally {
        $writer.Dispose()
    }
}

if ($Desktop) {
    $overlay = Join-Path $output 'overlay.png'
    $panel = Join-Path $output 'panel.png'
    $review = Join-Path $output 'review.png'
    $demo = Join-Path $output 'synthetic-sessions'
    New-Item -ItemType Directory -Path $demo -Force | Out-Null
    Write-DemoSession (Join-Path $demo 'network-20260101-101000-example.jsonl') '2026-01-01T10:10:00Z' 60 43 24
    Write-DemoSession (Join-Path $demo 'network-20260101-100000-example.jsonl') '2026-01-01T10:00:00Z' 48 57 28

    Invoke-Check 'overlay' ('--self-test-overlay {{report}} "{0}"' -f $overlay)
    Invoke-Check 'panel' ('--self-test-panel {{report}} "{0}"' -f $panel)
    Invoke-Check 'review' ('--self-test-review "{0}" {{report}} "{1}"' -f $demo,$review)
}

# Can assy-cli report a result for a path holding characters the ANSI codepage cannot encode?
#
#   .\agentic\tools\check-cli-encoding.ps1 -Video <path> -Subtitle <path> [-Env @{ NAME = 'value' }]
#
# Three real library titles fail this way, every one of them after a successful sync: the engine
# writes its output and then dies encoding its own JSON result to stdout. See U1 in AUDIT.md.
# -Env sets extra child variables so a candidate fix can be tried without rebuilding anything.

param(
    [Parameter(Mandatory = $true)][string]$Video,
    [Parameter(Mandatory = $true)][string]$Subtitle,
    [string]$Exe,
    [hashtable]$Env = @{},
    [string]$Label = 'run'
)

if (-not $Exe) {
    $Exe = Join-Path $PSScriptRoot '..\payload\assy-cli\win-x64\assy-cli.exe'
}

foreach ($path in @($Exe, $Video, $Subtitle)) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Error "not found: $path"
        exit 2
    }
}

$out = Join-Path ([IO.Path]::GetTempPath()) ("cliencoding-" + [Guid]::NewGuid().ToString('N') + '.srt')

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Resolve-Path -LiteralPath $Exe).Path

# ArgumentList is .NET Core only, so 5.1 needs the quoted string. No argument ends in a slash.
$psi.Arguments = @('--no-color', 'sync', """$Video""", """$Subtitle""", '-o', """$out""",
                   '-t', 'ffsubsync', '--json', '--encoding', 'same_as_input', '--no-prefix') -join ' '

$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

# The plugin allowlists rather than inherits; mirror it so only -Env differs between runs.
$psi.EnvironmentVariables.Clear()
foreach ($n in @('SystemRoot', 'windir', 'COMSPEC', 'PATHEXT', 'NUMBER_OF_PROCESSORS',
                 'USERPROFILE', 'HOME', 'TMP', 'TEMP', 'PATH')) {
    $v = [Environment]::GetEnvironmentVariable($n)
    if ($v) { $psi.EnvironmentVariables[$n] = $v }
}
foreach ($n in @('OMP_NUM_THREADS', 'OPENBLAS_NUM_THREADS', 'MKL_NUM_THREADS',
                 'NUMEXPR_NUM_THREADS', 'VECLIB_MAXIMUM_THREADS')) {
    $psi.EnvironmentVariables[$n] = '1'
}
foreach ($key in $Env.Keys) { $psi.EnvironmentVariables[$key] = $Env[$key] }

$watch = [Diagnostics.Stopwatch]::StartNew()
$process = [Diagnostics.Process]::Start($psi)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()
$watch.Stop()

$o = $stdout.Result
$e = $stderr.Result
$wrote = Test-Path -LiteralPath $out
$crash = ($e -split "`n" | Select-String 'UnicodeEncodeError|charmap').Line

$extra = if ($Env.Count -gt 0) { ($Env.Keys | ForEach-Object { "$_=$($Env[$_])" }) -join ' ' } else { '<none>' }

"===== $Label ====="
"  extra env  : $extra"
"  exit code  : $($process.ExitCode)   elapsed $([int]$watch.Elapsed.TotalSeconds)s"
"  stdout     : $($o.Length) chars" + $(if ($o.Trim()) { " -> " + ($o.Trim() -split "`n")[-1] } else { '' })
"  wrote -o   : $wrote"
if ($crash) {
    "  RESULT     : LOST - the sync ran but the result could not be reported"
    $crash | Select-Object -First 1 | ForEach-Object { "               " + $_.Trim() }
}
else {
    "  RESULT     : reported cleanly"
}

if ($wrote) { Remove-Item -LiteralPath $out -Force }
exit $(if ($crash) { 1 } else { 0 })

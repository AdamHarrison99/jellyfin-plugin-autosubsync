param(
    [Parameter(Mandatory = $true)][string]$OutPath,
    [ValidateSet('Solid', 'Outline')][string]$Style = 'Solid',
    [string]$FontName = 'Arial',
    [int]$FontSize = 45
)

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -Path (Join-Path $here 'SupWriter.cs') -ReferencedAssemblies 'System.Drawing'

$texts = @(
    'When I was lying there in the VA hospital,',
    'with a big hole blown through the middle of my life,',
    'I started having these dreams of flying.',
    'It cost 1,250 dollars in 1987.',
    '"Don''t move," she whispered.',
    'Isn''t it strange? I think so.'
)
$starts = @(1000, 5000, 9000, 13000, 17000, 21000)
$ends = @(4000, 8000, 12000, 16000, 20000, 24000)

[SupWriter]::Write($OutPath, $Style, $FontName, $FontSize, $texts, $starts, $ends)
Write-Output ('{0} ({1} bytes)' -f $OutPath, (Get-Item $OutPath).Length)

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = $PSScriptRoot
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $project 'dist\VlcPatchManager.exe'
$manifest = Join-Path $project 'app.manifest'
$source = Join-Path $project 'src\Program.cs'

if (-not (Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler was not found.' }

$arguments = @(
    '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+',
    "/win32manifest:$manifest", "/out:$output",
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll',
    '/reference:System.Xml.Linq.dll',
    '/reference:System.IO.Compression.dll',
    '/reference:System.IO.Compression.FileSystem.dll',
    $source
)
& $compiler $arguments

if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }
Get-Item -LiteralPath $output

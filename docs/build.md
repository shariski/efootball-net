# Build and verify

Use Windows x64 with the .NET 10 SDK. Clone this repo and run the commands from its root.

```powershell
dotnet restore .\src\GameDesk\eFootballNet.Windows.csproj -r win-x64 --locked-mode
.\tools\build.ps1
```

`tools/build.ps1` builds a standalone ZIP and a slim ZIP, then writes `SHA256SUMS.txt` beside them under `artifacts\`. The app version comes from the project file. The slim ZIP requires the .NET 10 Desktop Runtime. The standalone ZIP contains its copy of the runtime and includes its license and notices.

For a normal development build and the data smoke test:

```powershell
dotnet build .\src\GameDesk\eFootballNet.Windows.csproj -c Release -r win-x64 --no-restore
$app = '.\src\GameDesk\bin\Release\net10.0-windows\win-x64\eFootballNet.exe'
.\tools\smoke.ps1 -App $app
```

Use `-Trace` from an elevated terminal to verify that the two-process collector shuts down and that real IPv4 and IPv6 loopback traffic is observed. Use `-Desktop` from the signed-in desktop to verify overlay geometry, panel controls, and review with synthetic sessions. Set `-OutputDirectory` to keep the JSON reports and synthetic screenshots.

## Maintain a release

Update the project version and changelog. Build both packages, check their checksums, and run the elevated and desktop smoke tests against the packaged executable. Verify the installed portable copy with a live match when possible. Tag the tested commit and attach the two ZIP files and `SHA256SUMS.txt` to its GitHub release.

The provider table is generated from the AWS and Google Cloud prefix feeds. Run `python tools/update-providers.py` to refresh it. The script requests public prefix tables and emits a normalized, compressed prefix-to-region mapping.

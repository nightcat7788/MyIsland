$ErrorActionPreference = 'Continue'
"$PSHOME"
(Get-Item "$PSHOME\pwsh.exe").FullName
$PSVersionTable.PSVersion.ToString()

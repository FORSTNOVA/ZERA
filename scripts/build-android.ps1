param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$sdkPath=Join-Path $env:LOCALAPPDATA 'Android/Sdk'
$javaCandidates=@($env:JAVA_HOME,(Join-Path $env:USERPROFILE '.jdks/jbr-21.0.11'),'C:/Program Files/Android/Android Studio/jbr')
$javaPath=$javaCandidates | Where-Object {$_ -and (Test-Path -LiteralPath (Join-Path $_ 'bin/javac.exe'))} | Select-Object -First 1
if(!$javaPath){throw 'Install JDK 21 or set JAVA_HOME.'}
if(!(Test-Path -LiteralPath $sdkPath)){throw 'Android SDK not found in LOCALAPPDATA/Android/Sdk.'}
Push-Location $projectRoot
try {
    dotnet publish src/ZERA.Android/ZERA.Android.csproj -c $Configuration -f net10.0-android "-p:AndroidSdkDirectory=$sdkPath" "-p:JavaSdkDirectory=$javaPath" -p:AndroidPackageFormats=apk
    if($LASTEXITCODE){throw 'Android build failed.'}
    $apk=Get-ChildItem "src/ZERA.Android/bin/$Configuration/net10.0-android" -Recurse -Filter '*-Signed.apk' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if(!$apk){throw 'Signed APK not found.'}
    New-Item -ItemType Directory -Path dist -Force | Out-Null
    $destination=Join-Path $projectRoot 'dist/ZERA-Android-3.0.0-preview.2.apk'
    Copy-Item -LiteralPath $apk.FullName -Destination $destination -Force
    Get-FileHash -LiteralPath $destination -Algorithm SHA256
} finally {Pop-Location}

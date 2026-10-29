param([int]$Port=5098)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$log=Join-Path $root 'artifacts/http-checks'
[IO.Directory]::CreateDirectory($log)|Out-Null
$env:ASPNETCORE_ENVIRONMENT='Production'
$env:Limits__ApiKey='local-smoke-key'
$assembly=Join-Path $root 'src/Invoice.Api/bin/Release/net8.0/Mitzh.Invoice.Api.dll'
if(!(Test-Path $assembly)){throw 'Build Invoice.Api.sln -c Release before HTTP checks.'}
$process=Start-Process dotnet -ArgumentList @($assembly,'--urls',"http://127.0.0.1:$Port") -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $log 'stdout.log') -RedirectStandardError (Join-Path $log 'stderr.log')
$client=[Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.Add('X-Api-Key','local-smoke-key')
try{
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    $ready=$false
    while([DateTime]::UtcNow -lt $deadline){
        if($process.HasExited){throw 'API test process exited before startup.'}
        try{$response=$client.GetAsync("http://127.0.0.1:$Port/health").GetAwaiter().GetResult();if($response.IsSuccessStatusCode){$ready=$true;break}}catch{}
        Start-Sleep -Milliseconds 200
    }
    if(!$ready){throw 'API startup timeout.'}
    & (Join-Path $PSScriptRoot 'Smoke.ps1') -BaseUrl "http://127.0.0.1:$Port" -Fixtures (Join-Path $PSScriptRoot 'fixtures')
    & (Join-Path $PSScriptRoot 'XmlSmoke.ps1') -BaseUrl "http://127.0.0.1:$Port"
    & (Join-Path $PSScriptRoot 'ResponseSmoke.ps1') -BaseUrl "http://127.0.0.1:$Port"
    Write-Output '49 API HTTP/response checks passed.'
}finally{
    $client.Dispose()
    if(!$process.HasExited){Stop-Process -Id $process.Id -Force}
}
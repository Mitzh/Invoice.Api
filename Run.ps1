param([int]$Port = 5088)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:ASPNETCORE_ENVIRONMENT = 'Production'
if ([string]::IsNullOrWhiteSpace($env:Limits__ApiKey)) { throw 'Set $env:Limits__ApiKey to your private API key before starting.' }
$env:Limits__DetModel = Join-Path $projectRoot 'models/ch_PP-OCRv5_mobile_det.onnx'
$env:Limits__ClsModel = Join-Path $projectRoot 'models/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx'
$env:Limits__RecModel = Join-Path $projectRoot 'models/ch_PP-OCRv5_rec_mobile.onnx'
$env:Limits__Dictionary = Join-Path $projectRoot 'models/ppocrv5_dict.txt'
$tempDir = Join-Path $projectRoot 'src/Invoice.Api/App_Data/multipart'
New-Item -ItemType Directory -Force $tempDir | Out-Null
$env:ASPNETCORE_TEMP = $tempDir
dotnet run --project (Join-Path $projectRoot 'src/Invoice.Api') --no-launch-profile --urls "http://127.0.0.1:$Port"
exit $LASTEXITCODE

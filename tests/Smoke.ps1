param([string]$BaseUrl = 'http://127.0.0.1:5088', [string]$ApiKey = 'local-smoke-key', [Parameter(Mandatory)][string]$Fixtures)
function ConvertFrom-ApiJson { param([Parameter(ValueFromPipeline=$true)]$Text) process { $value=$Text|ConvertFrom-Json; if($value.code -ne 0 -or $null -ne $value.msg){throw "Unexpected API envelope"}; $value.data } }
$ErrorActionPreference = 'Stop'
$client = [System.Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.Add('X-Api-Key', $ApiKey)
function Assert($condition, $name) { if (!$condition) { throw "FAIL: $name" }; Write-Output "PASS: $name" }
function Upload($name, $overrideName) {
    $content = [System.Net.Http.MultipartFormDataContent]::new()
    $stream = [System.IO.File]::OpenRead((Join-Path $Fixtures $name))
    $part = [System.Net.Http.StreamContent]::new($stream)
    $content.Add($part, 'file', $(if ($overrideName) { $overrideName } else { $name }))
    try { $r = $client.PostAsync("$BaseUrl/api/invoices", $content).GetAwaiter().GetResult(); return @{Status=[int]$r.StatusCode; Body=$r.Content.ReadAsStringAsync().GetAwaiter().GetResult()} }
    finally { $content.Dispose() }
}
function Poll($id) {
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    do {
        $response=$client.GetAsync("$BaseUrl/api/jobs/$id").GetAwaiter().GetResult()
        $body=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if($body.code -eq 1){return @{state='Failed';error=$body.msg}}
        $r=$body.data
        if ($r.state -notin @('Queued','Parsing')) { return $r }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Polling timeout'
}
$anonymous=[System.Net.Http.HttpClient]::new()
Assert ([int]$anonymous.GetAsync("$BaseUrl/health").GetAwaiter().GetResult().StatusCode -eq 401) 'API key enforced'
$swagger=$anonymous.GetStringAsync("$BaseUrl/swagger/v1/swagger.json").GetAwaiter().GetResult()|ConvertFrom-Json
Assert ($swagger.components.securitySchemes.ApiKey.name -eq 'X-Api-Key') 'Swagger API-key authorization scheme'
$requestBody=$swagger.paths.'/api/invoices/recognize'.post.requestBody.content
Assert ($requestBody.'multipart/form-data'.schema.properties.file.format -eq 'binary' -and $requestBody.'application/octet-stream'.schema.format -eq 'binary') 'Swagger file picker and raw stream contracts'
Assert ($swagger.paths.'/api/invoices/recognize'.post.responses.'200'.content.'application/json'.schema.'$ref' -eq '#/components/schemas/InvoiceModelApiResponse') 'Swagger response wraps InvoiceModel'
$swaggerUi=$anonymous.GetStringAsync("$BaseUrl/swagger/index.html").GetAwaiter().GetResult()
Assert ($swaggerUi.Contains('swagger-ui-bundle.js')) 'Swagger UI available before authorization'
Assert ([int]$anonymous.GetAsync("$BaseUrl/swagger/swagger-ui-bundle.js").GetAwaiter().GetResult().StatusCode -eq 200) 'Swagger JavaScript served locally'
$anonymous.Dispose()
$h=$client.GetStringAsync("$BaseUrl/health").GetAwaiter().GetResult() | ConvertFrom-ApiJson
Assert ($h.status -eq 'ok' -and $h.ocrConfigured -eq $true) 'service and OCR configuration'
$raw = [System.Net.Http.ByteArrayContent]::new([System.IO.File]::ReadAllBytes((Join-Path $Fixtures 'synthetic.ofd')))
$direct=$client.PostAsync("$BaseUrl/api/invoices/recognize",$raw).GetAwaiter().GetResult()
Assert ([int]$direct.StatusCode -eq 200) 'direct binary stream returns 200'
$model=$direct.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-ApiJson
Assert ($model.number -eq '12345678901234567890' -and $model.items.Count -eq 2 -and !$model.PSObject.Properties['id']) 'direct endpoint returns InvoiceModel without job wrapper'
Assert ($model.requiresReview -and $model.warnings.Count -gt 0) 'direct model includes review warnings'
$raw.Dispose()
$multi=[System.Net.Http.MultipartFormDataContent]::new()
$multi.Add([System.Net.Http.ByteArrayContent]::new([System.IO.File]::ReadAllBytes((Join-Path $Fixtures 'synthetic.ofd'))),'file','invoice.ofd')
$direct=$client.PostAsync("$BaseUrl/api/invoices/recognize",$multi).GetAwaiter().GetResult()
Assert ([int]$direct.StatusCode -eq 200) 'direct multipart upload returns model'
$multi.Dispose()
$a=Upload 'synthetic.ofd' ''
Assert ($a.Status -eq 202) 'OFD accepted asynchronously'
$id=($a.Body|ConvertFrom-ApiJson).id
$first=Poll $id
Assert ($first.state -eq 'NeedsReview' -and $first.result.invoice.items.Count -eq 2) 'OFD job completed with two detail rows'
$a=Upload 'synthetic.ofd' ''; $second=Poll (($a.Body|ConvertFrom-ApiJson).id)
Assert (($second.result.issues -join '|').Contains('疑似重复')) 'duplicate identity linked within process'
$v=$client.GetStringAsync("$BaseUrl/api/jobs/$id/verification").GetAwaiter().GetResult() | ConvertFrom-ApiJson
Assert ($v.state -eq 'NotConfigured') 'tax verification is never fabricated'
foreach($f in @('text.pdf','synthetic.png','synthetic.jpg','scan.pdf')) {
    $r=Upload $f ''; Assert ($r.Status -eq 202) "$f accepted"
    $job=Poll (($r.Body|ConvertFrom-ApiJson).id)
    Assert ($job.state -eq 'NeedsReview' -and $job.result.evidence.Count -gt 0) "$f parsed through real engine"
}
$bad=Upload 'synthetic.png' 'fake.pdf'
Assert ($bad.Status -eq 415) 'signature mismatch rejected'
$bad=Upload 'synthetic.png' 'fake.exe'
Assert ($bad.Status -eq 415) 'unsupported extension rejected'
$a=Upload 'xxe.ofd' ''; $bad=Poll (($a.Body|ConvertFrom-ApiJson).id)
Assert ($bad.state -eq 'Failed' -and $bad.error) 'unsafe XML returns exception message'
$notFound=$client.GetAsync("$BaseUrl/api/jobs/$([guid]::NewGuid())").GetAwaiter().GetResult()
Assert ([int]$notFound.StatusCode -eq 404) 'unknown job returns 404'
$client.Dispose()



param([string]$BaseUrl='http://127.0.0.1:5088',[string]$ApiKey='local-smoke-key')
function ConvertFrom-ApiJson { param([Parameter(ValueFromPipeline=$true)]$Text) process { $value=$Text|ConvertFrom-Json; if($value.code -ne 0 -or $null -ne $value.msg){throw "Unexpected API envelope"}; $value.data } }
$ErrorActionPreference='Stop'
$xml=Get-Content -Raw -Encoding utf8 (Join-Path (Split-Path $PSScriptRoot -Parent) 'samples/einvoice.xml')
$client=[System.Net.Http.HttpClient]::new();$client.DefaultRequestHeaders.Add('X-Api-Key',$ApiKey)
$count=0
function Assert($ok,$name){if(!$ok){throw "FAIL: $name"};$script:count++;Write-Output "PASS: $name"}
function Send($route,$content){try{$r=$client.PostAsync("$BaseUrl$route",$content).GetAwaiter().GetResult();return @{Status=[int]$r.StatusCode;Text=$r.Content.ReadAsStringAsync().GetAwaiter().GetResult()}}finally{$content.Dispose()}}
function JsonContent($value){[System.Net.Http.StringContent]::new(($value|ConvertTo-Json -Depth 6),[Text.Encoding]::UTF8,'application/json')}
function FormContent($name){$c=[System.Net.Http.MultipartFormDataContent]::new();$c.Add([System.Net.Http.ByteArrayContent]::new([Text.Encoding]::UTF8.GetBytes($xml)),'file',$name);return ,$c}
function ValidModel($m){$m.number -eq '26000000000000000001' -and $m.total -eq 332 -and $m.items.Count -eq 2 -and $m.items[1].unitPrice -eq [decimal]'66.6666666666667'}
try{
 $r=Send '/api/invoices/recognize/xml' (JsonContent @{xml=$xml})
 Assert ($r.Status -eq 200 -and (ValidModel ($r.Text|ConvertFrom-ApiJson))) 'XML JSON content returns InvoiceModel'
 Assert (($r.Text|ConvertFrom-ApiJson).drawer -eq '测试开票人') 'XML content returns Drawer'
 $r=Send '/api/invoices/recognize' ([System.Net.Http.StringContent]::new($xml,[Text.Encoding]::UTF8,'application/xml'))
 Assert ($r.Status -eq 200 -and (ValidModel ($r.Text|ConvertFrom-ApiJson))) 'application/xml raw body returns InvoiceModel'
 $r=Send '/api/invoices/recognize' (FormContent 'invoice.xml')
 Assert ($r.Status -eq 200 -and (ValidModel ($r.Text|ConvertFrom-ApiJson))) 'XML original file multipart upload returns InvoiceModel'
 $utf16=$xml.Replace('encoding="utf-8"','encoding="utf-16"')
 $r=Send '/api/invoices/recognize' ([System.Net.Http.ByteArrayContent]::new([byte[]]([Text.Encoding]::Unicode.GetPreamble()+[Text.Encoding]::Unicode.GetBytes($utf16))))
 Assert ($r.Status -eq 200 -and (ValidModel ($r.Text|ConvertFrom-ApiJson))) 'UTF-16 XML raw stream supported'
 $r=Send '/api/invoices' (FormContent 'invoice.xml')
 Assert ($r.Status -eq 202) "XML file accepted by asynchronous upload (status $($r.Status))"
 $id=($r.Text|ConvertFrom-ApiJson).id;$deadline=[DateTime]::UtcNow.AddSeconds(30)
 do{$job=$client.GetStringAsync("$BaseUrl/api/jobs/$id").GetAwaiter().GetResult()|ConvertFrom-ApiJson;if($job.state -notin @('Queued','Parsing')){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
 Assert ($job.state -eq 'NeedsReview' -and (ValidModel $job.result.invoice)) 'XML background worker maps structured result'
 Assert ($job.result.evidence.Count -eq 0 -and $job.result.structuredFields.Count -gt 0) 'XML background result has structured evidence without OCR boxes'
 $verification=$client.GetStringAsync("$BaseUrl/api/jobs/$id/verification").GetAwaiter().GetResult()|ConvertFrom-ApiJson
 Assert ($verification.state -eq 'NotConfigured') 'XML parsing does not claim tax verification'
 $r=Send '/api/invoices/recognize/xml' (JsonContent @{xml=''})
 Assert ($r.Status -eq 400) 'empty XML content rejected with 400'
 $r=Send '/api/invoices/recognize/xml' (JsonContent @{xml='<EInvoice><EInvoiceData></EInvoice>'})
 Assert ($r.Status -eq 422) 'malformed XML rejected with 422'
 $r=Send '/api/invoices/recognize/xml' (JsonContent @{xml="<!DOCTYPE EInvoice [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><EInvoice><EInvoiceData>&x;</EInvoiceData></EInvoice>"})
 Assert ($r.Status -eq 422 -and !$r.Text.Contains('win.ini')) 'unsafe XML rejected without exposing entity content'
 $r=Send '/api/invoices/recognize/xml' (JsonContent @{xml='<Invoice><Number>123</Number></Invoice>'})
 Assert ($r.Status -eq 422) 'unsupported XML structure rejected'
 $r=Send '/api/invoices' (FormContent 'fake.pdf')
 Assert ($r.Status -eq 415) 'XML file masquerading as PDF rejected by async upload'
 $swagger=$client.GetStringAsync("$BaseUrl/swagger/v1/swagger.json").GetAwaiter().GetResult()|ConvertFrom-Json
 Assert ($swagger.paths.'/api/invoices/recognize/xml'.post.requestBody.content.'application/json'.example.xml.Contains('<EInvoice>')) 'Swagger provides a pasteable XML JSON example'
 Assert ($swagger.paths.'/api/invoices/recognize'.post.requestBody.content.'application/xml'.schema.type -eq 'string') 'Swagger exposes raw XML content type'
 $anonymous=[System.Net.Http.HttpClient]::new()
 try{$r=$anonymous.PostAsync("$BaseUrl/api/invoices/recognize/xml",(JsonContent @{xml=$xml})).GetAwaiter().GetResult();Assert ([int]$r.StatusCode -eq 401) 'XML content endpoint enforces API key'}finally{$anonymous.Dispose()}
 Write-Output "$count XML HTTP checks passed."
}finally{$client.Dispose()}


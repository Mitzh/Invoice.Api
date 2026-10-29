param([string]$BaseUrl='http://127.0.0.1:5088', [string]$ApiKey='local-smoke-key')
$ErrorActionPreference='Stop'
$client=[Net.Http.HttpClient]::new()
$client.DefaultRequestHeaders.Add('X-Api-Key',$ApiKey)
function CheckResponse($response,$status) {
    $text=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $json=$text|ConvertFrom-Json
    if([int]$response.StatusCode -ne $status -or $json.code -ne 1 -or !$json.msg -or $null -ne $json.data -or @($json.PSObject.Properties).Count -ne 3){throw "Invalid error envelope: $text"}
    Write-Output "PASS: HTTP $status error envelope"
}
try {
    CheckResponse ($client.GetAsync("$BaseUrl/api/jobs/00000000-0000-0000-0000-000000000000").GetAwaiter().GetResult()) 404
    $invalid=$client.PostAsync("$BaseUrl/api/invoices/recognize",[Net.Http.StringContent]::new('invalid')).GetAwaiter().GetResult()
    CheckResponse $invalid 422
    $message=($invalid.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json).msg
    if($message -ne 'Unsupported file content. Expected XML, OFD, PDF, PNG or JPEG.'){throw "Exception message was replaced: $message"}
    Write-Output 'PASS: parsing failure preserves original exception message'
    CheckResponse ($client.PostAsync("$BaseUrl/api/invoices/recognize/xml",[Net.Http.StringContent]::new('{bad',[Text.Encoding]::UTF8,'application/json')).GetAwaiter().GetResult()) 400
    $client.DefaultRequestHeaders.Remove('X-Api-Key')|Out-Null
    CheckResponse ($client.GetAsync("$BaseUrl/health").GetAwaiter().GetResult()) 401
} finally {$client.Dispose()}


# One-time setup for the tester log-diagnostics pipeline. Run ONCE with credentials that can
# create S3 buckets, IAM roles and Lambda functions (your normal admin profile):
#
#   powershell -File infra/diagnostics-s3-setup.ps1
#   powershell -File infra/diagnostics-s3-setup.ps1 -Profile myadmin
#
# Architecture (no credentials ever ship inside the mod, no anonymous write anywhere):
#
#   mod --GET--> Lambda Function URL  -->  returns a presigned S3 PUT URL that is
#                (public endpoint,          * for ONE exact object key we choose
#                 validates + signs)        * with the size baked into the signature
#                                           * expiring in 5 minutes
#   mod --PUT--> S3 (private bucket)
#
# The bucket has NO public access: uploads only happen through a signature this Lambda
# minted. A leaked URL is worth one upload of one declared size to one key, for 5 minutes.
#
# The mod degrades gracefully: if the endpoint is unset/unreachable/erroring, `uploadlogs`
# still writes the gzipped log locally and prints the path for the player to send manually.
#
# Reading the logs afterwards:
#   aws s3 ls s3://punkmultiverse-diagnostics/PunkMultiverse/logs/
#   aws s3 sync s3://punkmultiverse-diagnostics/PunkMultiverse/logs/<runId>/ ./logs-<runId>/

param(
    [string]$Profile = "",
    [string]$Bucket = "punkmultiverse-diagnostics",
    [string]$Region = "us-east-1",
    [string]$FunctionName = "punkmv-log-signer",
    [string]$RoleName = "punkmv-log-signer-role"
)

$ErrorActionPreference = "Stop"
$RepoDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$P = @()
if ($Profile) { $P = @("--profile", $Profile) }

function Aws { & aws @P @args }

Write-Host "== 1/6 bucket (private) =="
try { Aws s3api create-bucket --bucket $Bucket --region $Region | Out-Null } catch { Write-Host "  (bucket exists)" }
Aws s3api put-public-access-block --bucket $Bucket --public-access-block-configuration `
    "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true" | Out-Null

$lifecycle = '{"Rules":[{"ID":"expire-playtest-logs-30d","Filter":{"Prefix":"PunkMultiverse/logs/"},"Status":"Enabled","Expiration":{"Days":30}}]}'
$lifecycleFile = Join-Path $env:TEMP "punkmv-lifecycle.json"
Set-Content -Path $lifecycleFile -Value $lifecycle -Encoding Ascii
Aws s3api put-bucket-lifecycle-configuration --bucket $Bucket --lifecycle-configuration ("file://" + $lifecycleFile) | Out-Null
Write-Host "  bucket $Bucket is private, logs expire after 30 days"

Write-Host "== 2/6 execution role =="
$trust = '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"lambda.amazonaws.com"},"Action":"sts:AssumeRole"}]}'
$trustFile = Join-Path $env:TEMP "punkmv-trust.json"
Set-Content -Path $trustFile -Value $trust -Encoding Ascii
try {
    Aws iam create-role --role-name $RoleName --assume-role-policy-document ("file://" + $trustFile) | Out-Null
    Write-Host "  created $RoleName (waiting for IAM propagation)"
    Start-Sleep -Seconds 12
} catch { Write-Host "  (role exists)" }

# Only what the signer needs: put one object under the logs prefix.
$inline = @"
{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:PutObject"],"Resource":"arn:aws:s3:::$Bucket/PunkMultiverse/logs/*"}]}
"@
$inlineFile = Join-Path $env:TEMP "punkmv-signer-policy.json"
Set-Content -Path $inlineFile -Value $inline -Encoding Ascii
Aws iam put-role-policy --role-name $RoleName --policy-name punkmv-signer-put --policy-document ("file://" + $inlineFile) | Out-Null
Aws iam attach-role-policy --role-name $RoleName --policy-arn arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole | Out-Null
$RoleArn = (Aws iam get-role --role-name $RoleName --query "Role.Arn" --output text)
Write-Host "  role: $RoleArn"

Write-Host "== 3/6 package lambda =="
$zip = Join-Path $env:TEMP "punkmv-log-signer.zip"
Remove-Item -Force -ErrorAction SilentlyContinue $zip
$stage = Join-Path $env:TEMP "punkmv-signer-stage"
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $stage
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $RepoDir "infra\log_signer_lambda.py") (Join-Path $stage "lambda_function.py")
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Write-Host "  packaged $zip"

Write-Host "== 4/6 lambda =="
try {
    Aws lambda create-function --function-name $FunctionName --runtime python3.12 `
        --role $RoleArn --handler lambda_function.lambda_handler --timeout 10 --memory-size 256 `
        --environment ("Variables={BUCKET=" + $Bucket + "}") `
        --zip-file ("fileb://" + $zip) --region $Region | Out-Null
    Write-Host "  created $FunctionName"
} catch {
    Aws lambda update-function-code --function-name $FunctionName --zip-file ("fileb://" + $zip) --region $Region | Out-Null
    Aws lambda update-function-configuration --function-name $FunctionName `
        --environment ("Variables={BUCKET=" + $Bucket + "}") --region $Region | Out-Null
    Write-Host "  updated $FunctionName"
}

Write-Host "== 5/6 public function URL =="
try {
    Aws lambda create-function-url-config --function-name $FunctionName --auth-type NONE --region $Region | Out-Null
} catch { Write-Host "  (url exists)" }
try {
    Aws lambda add-permission --function-name $FunctionName --statement-id AllowPublicFunctionUrl `
        --action lambda:InvokeFunctionUrl --principal "*" --function-url-auth-type NONE --region $Region | Out-Null
} catch { Write-Host "  (invoke permission exists)" }
$Url = (Aws lambda get-function-url-config --function-name $FunctionName --query "FunctionUrl" --output text --region $Region)

Write-Host "== 6/6 done =="
Write-Host ""
Write-Host "Signer endpoint:  $Url"
Write-Host ""
Write-Host "Put this in each player's BepInEx/plugins/PunkMultiverse/config.cfg under [Diag]:"
Write-Host "  LogUploadEndpoint = $Url"
Write-Host ""
Write-Host "Smoke test (expect a JSON url, then HTTP 200 on the PUT):"
Write-Host "  curl.exe -s `"${Url}?runId=setuptest&player=probe&size=5`""
Write-Host ""
Write-Host "Confirm the bucket is NOT publicly readable (expect 403):"
Write-Host "  curl.exe -s -o NUL -w '%{http_code}' https://$Bucket.s3.amazonaws.com/PunkMultiverse/logs/setuptest/probe.log.gz"

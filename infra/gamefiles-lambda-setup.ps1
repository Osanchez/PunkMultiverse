# One-time setup for the PERMANENT game-files download URL used by the Pelican server egg
# (GAME_FILES_URL). Run ONCE with credentials that can create IAM roles and Lambda functions
# (your admin profile):
#
#   powershell -File infra/gamefiles-lambda-setup.ps1
#   powershell -File infra/gamefiles-lambda-setup.ps1 -Profile myadmin
#
# Architecture (bucket stays fully private; the URL never expires):
#
#   installer --GET--> Lambda Function URL  --302-->  fresh presigned S3 GET URL
#              (permanent, public,                     (short-lived, minted per call)
#               holds no data)
#   installer --GET--> S3 (private bucket)  -->  streams punk-base.tar.gz
#
# The game archive must already be uploaded to s3://<Bucket>/<Key> (a private object). Set
# GAME_FILES_URL on the Pelican server to the Function URL this prints, then Reinstall the server.
#
# NOTE: a Lambda-minted presigned URL is capped by the execution role's session (hours, not the
# 7-day IAM-user max) - fine, because a fresh one is minted on every install. EXPIRES only needs
# to outlast one download.
param(
    [string]$Profile = "",
    [string]$Bucket = "punkmultiverse-gamefiles-577792960632-us-east-1",
    [string]$Key = "punk-base.tar.gz",
    [string]$Region = "us-east-1",
    [string]$FunctionName = "punkmv-gamefiles-url",
    [string]$RoleName = "punkmv-gamefiles-url-role",
    [int]$Expires = 3600
)

$ErrorActionPreference = "Stop"
$RepoDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$P = @()
if ($Profile) { $P = @("--profile", $Profile) }

# NOTE: call aws.exe explicitly — a bareword `aws` resolves to THIS function (names are
# case-insensitive) and recurses to a call-depth overflow.
function Aws { & aws.exe @P @args }

Write-Host "== 1/5 verify private bucket + object =="
Aws s3api put-public-access-block --bucket $Bucket --public-access-block-configuration `
    "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true" | Out-Null
$exists = (Aws s3api head-object --bucket $Bucket --key $Key --query "ContentLength" --output text 2>$null)
if (-not $exists) { throw "s3://$Bucket/$Key not found - upload the game archive first (aws s3 cp punk-base.tar.gz s3://$Bucket/$Key)" }
Write-Host "  s3://$Bucket/$Key present ($exists bytes), bucket private"

Write-Host "== 2/5 execution role =="
$trust = '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"lambda.amazonaws.com"},"Action":"sts:AssumeRole"}]}'
$trustFile = Join-Path $env:TEMP "punkmv-gf-trust.json"
Set-Content -Path $trustFile -Value $trust -Encoding Ascii
try {
    Aws iam create-role --role-name $RoleName --assume-role-policy-document ("file://" + $trustFile) | Out-Null
    Write-Host "  created $RoleName (waiting for IAM propagation)"
    Start-Sleep -Seconds 12
} catch { Write-Host "  (role exists)" }

# Least privilege: read ONLY the one game object (enough to mint a working presigned GET).
$inline = @"
{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:GetObject"],"Resource":"arn:aws:s3:::$Bucket/$Key"}]}
"@
$inlineFile = Join-Path $env:TEMP "punkmv-gf-policy.json"
Set-Content -Path $inlineFile -Value $inline -Encoding Ascii
Aws iam put-role-policy --role-name $RoleName --policy-name punkmv-gamefiles-get --policy-document ("file://" + $inlineFile) | Out-Null
Aws iam attach-role-policy --role-name $RoleName --policy-arn arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole | Out-Null
$RoleArn = (Aws iam get-role --role-name $RoleName --query "Role.Arn" --output text)
if (-not $RoleArn -or $RoleArn -eq "None") {
    throw "Could not create/read IAM role '$RoleName'. This profile lacks IAM permissions " +
          "(iam:CreateRole/PutRolePolicy/AttachRolePolicy/GetRole). Re-run with an admin profile: " +
          "powershell -File infra/gamefiles-lambda-setup.ps1 -Profile <admin>"
}
Write-Host "  role: $RoleArn"

Write-Host "== 3/5 package lambda =="
$zip = Join-Path $env:TEMP "punkmv-gamefiles-url.zip"
Remove-Item -Force -ErrorAction SilentlyContinue $zip
$stage = Join-Path $env:TEMP "punkmv-gf-stage"
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $stage
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $RepoDir "infra\gamefiles_url_lambda.py") (Join-Path $stage "lambda_function.py")
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Write-Host "  packaged $zip"

Write-Host "== 4/5 lambda =="
$envVars = "Variables={BUCKET=$Bucket,KEY=$Key,EXPIRES=$Expires}"
try {
    Aws lambda create-function --function-name $FunctionName --runtime python3.12 `
        --role $RoleArn --handler lambda_function.lambda_handler --timeout 10 --memory-size 128 `
        --environment $envVars --zip-file ("fileb://" + $zip) --region $Region | Out-Null
    Write-Host "  created $FunctionName"
} catch {
    Aws lambda update-function-code --function-name $FunctionName --zip-file ("fileb://" + $zip) --region $Region | Out-Null
    Start-Sleep -Seconds 3
    Aws lambda update-function-configuration --function-name $FunctionName `
        --environment $envVars --region $Region | Out-Null
    Write-Host "  updated $FunctionName"
}

Write-Host "== 5/5 public function URL =="
try {
    Aws lambda create-function-url-config --function-name $FunctionName --auth-type NONE --region $Region | Out-Null
} catch { Write-Host "  (url exists)" }
try {
    Aws lambda add-permission --function-name $FunctionName --statement-id AllowPublicFunctionUrl `
        --action lambda:InvokeFunctionUrl --principal "*" --function-url-auth-type NONE --region $Region | Out-Null
} catch { Write-Host "  (invoke permission exists)" }
$Url = (Aws lambda get-function-url-config --function-name $FunctionName --query "FunctionUrl" --output text --region $Region)

Write-Host ""
Write-Host "== done =="
Write-Host "Permanent GAME_FILES_URL:  $Url"
Write-Host "Backing object:            s3://$Bucket/$Key (private)"
Write-Host ""
Write-Host "Set GAME_FILES_URL on the Pelican server to the URL above, then Reinstall the server."
Write-Host "Smoke test (expect HTTP 200 + a gzip download):"
Write-Host "  curl.exe -sSL -o punk-base.tar.gz `"$Url`""

import os
import boto3

# Permanent-URL front door for the private game archive. The Lambda Function URL is stable and
# public, but it holds no data — on each GET it mints a SHORT-LIVED presigned S3 GET URL and 302s
# to it. So GAME_FILES_URL (= this Function URL) never expires, while the bucket stays fully
# private and the signed link it hands out is fresh every install (valid EXPIRES seconds, capped
# by this Lambda role's session). A leaked Function URL is worth one download of one object.
_s3 = boto3.client("s3")
BUCKET = os.environ["BUCKET"]
KEY = os.environ["KEY"]
EXPIRES = int(os.environ.get("EXPIRES", "3600"))


def lambda_handler(event, context):
    try:
        url = _s3.generate_presigned_url(
            "get_object",
            Params={"Bucket": BUCKET, "Key": KEY},
            ExpiresIn=EXPIRES,
        )
    except Exception as e:  # never 500 silently — the installer surfaces the body
        return {
            "statusCode": 500,
            "headers": {"Content-Type": "text/plain"},
            "body": f"presign failed: {e}",
        }
    # 302 so `curl -L` (the installer uses curl -sSL) transparently follows to S3.
    return {
        "statusCode": 302,
        "headers": {"Location": url, "Cache-Control": "no-store"},
    }

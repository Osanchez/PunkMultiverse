"""
PunkMultiverse tester log-upload signer.

Hands out a SHORT-LIVED, SINGLE-OBJECT presigned S3 PUT URL so the game mod can
upload a run log without carrying any AWS credentials (a shipped DLL is trivially
decompiled). The bucket itself stays fully private — no anonymous access at all.

Abuse bounds, all enforced here rather than by a broad bucket policy:
  - key shape is chosen by US, not the caller (caller only supplies ids we validate)
  - one URL = one exact object key
  - ContentLength is baked into the signature: the PUT must be exactly the size the
    caller declared, so a leaked URL can't be used to upload something huge
  - 5-minute expiry
  - hard size ceiling
  - the key is STABLE per (run, player): re-sending OVERWRITES that player's object
    instead of adding another. Spam-clicking therefore cannot grow a run's folder at
    all — a run holds exactly one object per player, forever. (Repeat-send cost is
    bounded client-side by a cooldown + per-run cap, and globally by the function's
    reserved concurrency.)

Request:  GET <function-url>?runId=<id>&player=<name>&pid=<stable-id>&size=<bytes>
Response: 200 {"url": "...", "key": "...", "expiresIn": 300}
          400 {"error": "..."}
"""

import json
import os
import re

import boto3

BUCKET = os.environ["BUCKET"]
PREFIX = "PunkMultiverse/logs"
MAX_BYTES = 10 * 1024 * 1024  # 10 MiB gzipped (real logs run ~6 KiB; this is a hard ceiling)
EXPIRY_SECONDS = 300

_s3 = boto3.client("s3")

# Ids are mod-generated and already sanitized client-side; re-validate here because
# this endpoint is public and the key is built from them.
_SAFE = re.compile(r"^[A-Za-z0-9_-]{1,64}$")


def lambda_handler(event, _context):
    params = event.get("queryStringParameters") or {}
    run_id = (params.get("runId") or "").strip()
    player = (params.get("player") or "").strip()
    pid = (params.get("pid") or "").strip()
    raw_size = (params.get("size") or "").strip()

    if not _SAFE.match(run_id):
        return _bad("invalid runId")
    if not _SAFE.match(player):
        return _bad("invalid player")
    if not _SAFE.match(pid):
        return _bad("invalid pid")
    try:
        size = int(raw_size)
    except ValueError:
        return _bad("invalid size")
    if size <= 0 or size > MAX_BYTES:
        return _bad("size out of range")

    # STABLE key — no timestamp. `player` is the display name (readable); `pid` is a short
    # hash of the player's stable identity, which disambiguates two players sharing a name.
    # Re-sends land on this same key and overwrite.
    key = "{}/{}/{}-{}.log.gz".format(PREFIX, run_id, player, pid)
    url = _s3.generate_presigned_url(
        "put_object",
        Params={
            "Bucket": BUCKET,
            "Key": key,
            "ContentType": "application/gzip",
            "ContentLength": size,
        },
        ExpiresIn=EXPIRY_SECONDS,
        HttpMethod="PUT",
    )
    return {
        "statusCode": 200,
        "headers": {"content-type": "application/json"},
        "body": json.dumps({"url": url, "key": key, "expiresIn": EXPIRY_SECONDS}),
    }


def _bad(message):
    return {
        "statusCode": 400,
        "headers": {"content-type": "application/json"},
        "body": json.dumps({"error": message}),
    }

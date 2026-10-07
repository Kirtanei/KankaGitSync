"""Drop one forwarded Kanka API response without recording credential-bearing data.

Run through mitmdump. The PowerShell launcher supplies the allowed method and path.
"""

import json
import os
from pathlib import Path

from mitmproxy import http

TARGET_HOST = "api.kanka.io"
TARGET_METHOD = os.environ["KANKA_FAULT_METHOD"].upper()
TARGET_PATH = os.environ["KANKA_FAULT_PATH"]
EVIDENCE = Path(os.environ["KANKA_FAULT_EVIDENCE"])
triggered = False


def write_event(event: dict) -> None:
    safe = {key: value for key, value in event.items() if key in {"event", "method", "path", "status"}}
    EVIDENCE.write_text(json.dumps(safe, separators=(",", ":")) + "\n", encoding="utf-8")


def request(flow: http.HTTPFlow) -> None:
    if flow.request.host != TARGET_HOST:
        flow.response = http.Response.make(403, b"Proxy target rejected")
        return
    if flow.request.method.upper() == TARGET_METHOD and flow.request.path == TARGET_PATH:
        flow.metadata["kanka_fault_target"] = True


def response(flow: http.HTTPFlow) -> None:
    global triggered
    if triggered or not flow.metadata.get("kanka_fault_target"):
        return
    triggered = True
    write_event({
        "event": "origin-response-suppressed",
        "method": flow.request.method.upper(),
        "path": flow.request.path,
        "status": flow.response.status_code,
    })
    # The origin response was fully received. Closing the client-side flow models a lost response.
    flow.kill()

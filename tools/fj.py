#!/usr/bin/env python3
"""Small Forgejo (Gitea v1 API) helper for the local RHI-Linux tracker.

Configuration comes from the environment only; this file holds no secrets.

  FORGEJO_BASE    e.g. http://127.0.0.1:3000 or http://127.0.0.1:3000/api/v1
  FORGEJO_OWNER   repository owner (user or org)
  FORGEJO_REPO    repository name
  FORGEJO_TOKEN   API token (needs write:issue for issue/label/milestone writes)
  FORGEJO_CRED_FILE  optional KEY=VALUE file; its values override the inherited environment

Every call first checks that the API reports exactly FORGEJO_OWNER/FORGEJO_REPO, and HTTP
redirects are refused (a redirected POST would silently become a GET).

Bodies are always passed as FILE PATHS (long inline text breaks shells and quoting).

Commands:
  repo                                   print repository summary
  labels                                 list labels (id, name)
  label-create NAME COLOUR [DESCRIPTION] create label if absent (colour like e11d21)
  milestones                             list milestones
  milestone-create TITLE [DESCRIPTION_FILE]
  issues [--state open|closed|all]       list issues (number, state, title)
  issue-get N                            print issue JSON
  issue-create --title T --body-file F [--label L ...] [--milestone M]
                                         create issue; labels/milestone by name; prints number
  issue-edit N [--title T] [--body-file F]
  issue-labels N L [L ...]               replace the issue's label set (names)
  comment N FILE                         add a comment from FILE
  pin N                                  pin the issue (Forgejo >= 1.22 API)
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from typing import Any


def _load_cred_file() -> None:
    path = os.environ.get("FORGEJO_CRED_FILE")
    if not path:
        return
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, value = line.split("=", 1)
            # The named file is the explicit choice: it overrides anything inherited from the
            # shell, so a stale FORGEJO_REPO from another project cannot redirect writes.
            os.environ[key.strip()] = value.strip().strip("'\"")


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """Refuse redirects. urllib turns a redirected POST into a GET, which hides a wrong or
    renamed repository behind a silent read."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise urllib.error.HTTPError(req.full_url, code, f"redirect to {newurl} refused", headers, fp)


_OPENER = urllib.request.build_opener(_NoRedirect)


def _cfg() -> tuple[str, str, str, str]:
    _load_cred_file()
    try:
        base = os.environ["FORGEJO_BASE"].rstrip("/")
        owner = os.environ["FORGEJO_OWNER"]
        repo = os.environ["FORGEJO_REPO"]
        token = os.environ["FORGEJO_TOKEN"]
    except KeyError as exc:
        sys.exit(f"missing environment variable {exc.args[0]}")
    if not base.endswith("/api/v1"):
        base += "/api/v1"
    return base, owner, repo, token


BASE, OWNER, REPO, TOKEN = "", "", "", ""


def api(method: str, path: str, payload=None, retries: int = 4) -> Any:
    """Call the API; retry on 429/5xx and connection errors with backoff."""
    url = path if path.startswith("http") else f"{BASE}{path}"
    data = None if payload is None else json.dumps(payload).encode()
    delay = 1.0
    for attempt in range(retries + 1):
        req = urllib.request.Request(url, data=data, method=method)
        req.add_header("Authorization", f"token {TOKEN}")
        req.add_header("Accept", "application/json")
        if data is not None:
            req.add_header("Content-Type", "application/json")
        try:
            with _OPENER.open(req, timeout=30) as resp:
                raw = resp.read()
                return json.loads(raw) if raw else None
        except urllib.error.HTTPError as err:
            body = err.read().decode(errors="replace")[:400]
            if err.code in (429, 500, 502, 503, 504) and attempt < retries:
                time.sleep(float(err.headers.get("Retry-After") or delay))
                delay *= 2
                continue
            sys.exit(f"{method} {path} -> HTTP {err.code}: {body}")
        except urllib.error.URLError as err:
            if attempt < retries:
                time.sleep(delay)
                delay *= 2
                continue
            sys.exit(f"{method} {path} -> {err}")
    return None


def repo_path(suffix: str = "") -> str:
    return f"/repos/{OWNER}/{REPO}{suffix}"


def paged(path: str) -> list:
    out, page = [], 1
    sep = "&" if "?" in path else "?"
    while True:
        batch = api("GET", f"{path}{sep}limit=50&page={page}") or []
        out.extend(batch)
        if len(batch) < 50:
            return out
        page += 1


def label_ids(names: list[str]) -> list[int]:
    by_name = {l["name"]: l["id"] for l in paged(repo_path("/labels"))}
    missing = [n for n in names if n not in by_name]
    if missing:
        sys.exit(f"unknown label(s): {', '.join(missing)}")
    return [by_name[n] for n in names]


def milestone_id(title: str) -> int:
    for m in paged(repo_path("/milestones?state=all")):
        if m["title"] == title:
            return m["id"]
    sys.exit(f"unknown milestone: {title}")


def read_file(path: str) -> str:
    with open(path, encoding="utf-8") as fh:
        return fh.read()


def main() -> None:
    global BASE, OWNER, REPO, TOKEN
    BASE, OWNER, REPO, TOKEN = _cfg()

    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("repo")
    sub.add_parser("labels")
    s = sub.add_parser("label-create"); s.add_argument("name"); s.add_argument("colour"); s.add_argument("description", nargs="?", default="")
    sub.add_parser("milestones")
    s = sub.add_parser("milestone-create"); s.add_argument("title"); s.add_argument("description_file", nargs="?")
    s = sub.add_parser("issues"); s.add_argument("--state", default="open")
    s = sub.add_parser("issue-get"); s.add_argument("number", type=int)
    s = sub.add_parser("issue-create"); s.add_argument("--title", required=True); s.add_argument("--body-file", required=True)
    s.add_argument("--label", action="append", default=[]); s.add_argument("--milestone")
    s = sub.add_parser("issue-edit"); s.add_argument("number", type=int); s.add_argument("--title"); s.add_argument("--body-file")
    s = sub.add_parser("issue-labels"); s.add_argument("number", type=int); s.add_argument("labels", nargs="+")
    s = sub.add_parser("comment"); s.add_argument("number", type=int); s.add_argument("file")
    s = sub.add_parser("pin"); s.add_argument("number", type=int)
    a = p.parse_args()

    # Identity check: the API must report exactly the configured repository.
    identity = api("GET", repo_path())
    if identity.get("full_name", "").lower() != f"{OWNER}/{REPO}".lower():
        sys.exit(f"repository mismatch: configured {OWNER}/{REPO}, API reports {identity.get('full_name')}")

    if a.cmd == "repo":
        r = api("GET", repo_path())
        print(json.dumps({k: r.get(k) for k in ("full_name", "private", "has_issues", "html_url", "default_branch")}, indent=2))
    elif a.cmd == "labels":
        for l in paged(repo_path("/labels")):
            print(f"{l['id']}\t{l['name']}")
    elif a.cmd == "label-create":
        existing = {l["name"] for l in paged(repo_path("/labels"))}
        if a.name in existing:
            print(f"exists\t{a.name}")
        else:
            r = api("POST", repo_path("/labels"), {"name": a.name, "color": "#" + a.colour.lstrip("#"), "description": a.description})
            print(f"{r['id']}\t{r['name']}")
    elif a.cmd == "milestones":
        for m in paged(repo_path("/milestones?state=all")):
            print(f"{m['id']}\t{m['state']}\t{m['title']}")
    elif a.cmd == "milestone-create":
        existing = {m["title"] for m in paged(repo_path("/milestones?state=all"))}
        if a.title in existing:
            print(f"exists\t{a.title}")
        else:
            desc = read_file(a.description_file) if a.description_file else ""
            r = api("POST", repo_path("/milestones"), {"title": a.title, "description": desc})
            print(f"{r['id']}\t{r['title']}")
    elif a.cmd == "issues":
        for i in paged(repo_path(f"/issues?type=issues&state={a.state}")):
            print(f"{i['number']}\t{i['state']}\t{i['title']}")
    elif a.cmd == "issue-get":
        print(json.dumps(api("GET", repo_path(f"/issues/{a.number}")), indent=2))
    elif a.cmd == "issue-create":
        payload = {"title": a.title, "body": read_file(a.body_file)}
        if a.label:
            payload["labels"] = label_ids(a.label)
        if a.milestone:
            payload["milestone"] = milestone_id(a.milestone)
        r = api("POST", repo_path("/issues"), payload)
        got = api("GET", repo_path(f"/issues/{r['number']}"))
        if got["title"] != a.title or got["body"] != payload["body"]:
            sys.exit(f"readback mismatch on #{r['number']}")
        print(r["number"])
    elif a.cmd == "issue-edit":
        payload = {}
        if a.title:
            payload["title"] = a.title
        if a.body_file:
            payload["body"] = read_file(a.body_file)
        api("PATCH", repo_path(f"/issues/{a.number}"), payload)
        got = api("GET", repo_path(f"/issues/{a.number}"))
        if "body" in payload and got["body"] != payload["body"]:
            sys.exit(f"body did not update on #{a.number}")
        print(a.number)
    elif a.cmd == "issue-labels":
        # PUT replaces the whole set; labels in a PATCH body are silently ignored.
        api("PUT", repo_path(f"/issues/{a.number}/labels"), {"labels": label_ids(a.labels)})
        got = sorted(l["name"] for l in api("GET", repo_path(f"/issues/{a.number}"))["labels"])
        print("\t".join(got))
    elif a.cmd == "comment":
        r = api("POST", repo_path(f"/issues/{a.number}/comments"), {"body": read_file(a.file)})
        print(r["id"])
    elif a.cmd == "pin":
        api("POST", repo_path(f"/issues/{a.number}/pin"))
        print(f"pinned #{a.number}")


if __name__ == "__main__":
    main()

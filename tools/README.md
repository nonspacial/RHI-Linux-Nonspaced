# tools

## fj.py — local Forgejo tracker helper

`fj.py` talks to the local Forgejo issue tracker for this fork (repo `infra/RHI-Linux`).
It uses only the Python standard library and reads every setting from the environment.
It holds no secrets.

### Set-up

Keep the credentials in an env file outside the repository, mode `600`:

```sh
FORGEJO_BASE=http://127.0.0.1:3000/api/v1
FORGEJO_OWNER=infra
FORGEJO_REPO=RHI-Linux
FORGEJO_TOKEN=<token with write:issue>
```

Load it one of two ways:

```sh
set -a; source /path/to/forgejo-rhi.env; set +a        # export into the shell
FORGEJO_CRED_FILE=/path/to/forgejo-rhi.env tools/fj.py labels   # or let fj.py read it (file values win)
```

Every run first checks that the API reports exactly `FORGEJO_OWNER/FORGEJO_REPO`, and refuses HTTP redirects.
A stale `FORGEJO_REPO` inherited from another project therefore cannot send writes elsewhere.

Do not echo the token, and do not commit the env file.

### Usage

```sh
tools/fj.py repo
tools/fj.py labels
tools/fj.py milestones
tools/fj.py issues --state all
tools/fj.py issue-get 12
tools/fj.py issue-create --title "DET-05 …" --body-file /tmp/body.md \
    --label audit:detection --label P1 --milestone audit-remediation
tools/fj.py issue-edit 12 --body-file /tmp/body.md
tools/fj.py issue-labels 12 audit:detection P2      # replaces the full set
tools/fj.py comment 12 /tmp/comment.md
tools/fj.py pin 1
```

Notes:

- Pass bodies and comments as **file paths**. Long inline text breaks quoting.
- Labels and milestones are given **by name**. The script resolves them to IDs.
- `issue-create` and `issue-edit` re-read the issue and fail if the stored body differs.
- `issue-labels` uses `PUT …/labels`. Labels sent in a `PATCH` body are silently ignored by Forgejo.
- Calls retry on 429/5xx with backoff.

### Pushing

`git push forge <branch>`. The `forge` remote is the local Forgejo repo. **Never push to `origin`**, which is GitHub; Robbie pushes there himself.
Give credentials through a short-lived `GIT_ASKPASS` script. Never embed a token in the remote URL or `.git-credentials`.

"""Manual test of the Azure DevOps Server 2020 pull request flow (api-version 6.0).

Standard library only; requests are sent with curl.exe (Windows TLS). Certificate checks stay ON.
Creates a REAL pull request, then offers to abandon it. Use a throwaway branch.

Run:  python ado-pr-test.py
"""
import getpass
import json
import os
import subprocess
import sys
import tempfile
import urllib.parse

API = "6.0"


def ask(label, default=None, secret=False):
    shown = f" [{default}]" if default else ""
    prompt = f"{label}{shown}: "
    value = getpass.getpass(prompt) if secret else input(prompt)
    return value.strip() or (default or "")


def call(method, url, pat, body=None):
    """Returns (status, parsed_json_or_text). Never raises on HTTP errors."""
    # The PAT goes to curl through stdin (-K -), so it never appears in a process list.
    escaped = pat.replace("\\", "\\\\").replace('"', '\\"')
    cmd = ["curl.exe", "-s", "-S", "--max-time", "60", "-K", "-", "-X", method,
           "-H", "Accept: application/json", "-w", "\n%{http_code}"]
    body_file = None
    if body is not None:
        body_file = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8")
        json.dump(body, body_file)
        body_file.close()
        cmd += ["-H", "Content-Type: application/json", "--data-binary", f"@{body_file.name}"]
    cmd.append(url)
    try:
        run = subprocess.run(cmd, input=f'user = ":{escaped}"\n', capture_output=True,
                             text=True, encoding="utf-8", errors="replace")
    except FileNotFoundError:
        return None, "curl.exe not found on PATH"
    finally:
        if body_file:
            os.unlink(body_file.name)
    if run.returncode != 0:
        return None, f"curl error {run.returncode}: {run.stderr.strip()}"
    text, _, code = run.stdout.rpartition("\n")
    status = int(code) if code.strip().isdigit() else None
    try:
        return status, json.loads(text)
    except ValueError:
        return status, text[:500]


def step(title):
    print(f"\n=== {title} ===")


def show(status, payload, keys=None):
    print(f"HTTP {status}")
    if isinstance(payload, dict) and keys:
        payload = {k: payload.get(k) for k in keys}
    print(json.dumps(payload, indent=2, ensure_ascii=False) if not isinstance(payload, str) else payload)


def main():
    print("Azure DevOps PR flow test (creates a real PR; use a throwaway branch)")
    pat = os.environ.get("ADO_PAT") or ask("PAT (hidden)", secret=True)
    base = ask("Collection URL, e.g. https://myserver/value (no project, no /_git)", "https://server/value")
    if not base.endswith("/"):
        base += "/"  # a trailing slash is fine either way; urls below are built on it
    proj = ask("Project", "ace_bank")
    repo = ask("Repo", "ace_bank")
    src = ask("Source branch (throwaway, already pushed)")
    tgt = ask("Target branch", "uat")
    title = ask("PR title", "PR CR-1042 12345 into uat")
    if not (pat and src):
        sys.exit("PAT and source branch are required.")

    q = urllib.parse.quote
    repo_url = f"{base}{q(proj)}/_apis/git/repositories/{q(repo)}"

    step("1. Connection and your user id")
    st, data = call("GET", f"{base}_apis/connectionData", pat)
    show(st, data, ["authenticatedUser"] if isinstance(data, dict) else None)
    user_id = None
    if isinstance(data, dict):
        user_id = (data.get("authenticatedUser") or {}).get("id")
    if st != 200 or not user_id:
        sys.exit("Stopped: connection failed (check URL, PAT, certificate).")

    step("2. Repository")
    st, data = call("GET", f"{repo_url}?api-version={API}", pat)
    show(st, data, ["id", "name", "defaultBranch", "project"])
    if st != 200:
        sys.exit("Stopped: repository not found. Check collection, project and repo.")

    step("3. Source and target branches exist")
    for name in (src, tgt):
        st, data = call("GET", f"{repo_url}/refs?filter=heads/{q(name)}&api-version={API}", pat)
        found = [r["name"] for r in data.get("value", [])] if isinstance(data, dict) else data
        print(f"{name}: HTTP {st}, matches: {found}")

    step("4. Commits ahead (the pipeline's 'new commits' check)")
    params = urllib.parse.urlencode({
        "baseVersion": tgt, "baseVersionType": "branch",
        "targetVersion": src, "targetVersionType": "branch",
        "diffCommonCommit": "true", "$top": 1, "api-version": API,
    })
    st, data = call("GET", f"{repo_url}/diffs/commits?{params}", pat)
    show(st, data, ["aheadCount", "behindCount", "allChangesIncluded"] if isinstance(data, dict) else None)

    if ask("\nCreate a REAL PR now? (y/n)", "n").lower() != "y":
        sys.exit("Stopped before creating a PR.")

    body = {
        "sourceRefName": f"refs/heads/{src}",
        "targetRefName": f"refs/heads/{tgt}",
        "title": title,
        "description": "Created by Max Planner (test)",
    }
    pr_url = f"{repo_url}/pullrequests"

    step("5. Create the PR")
    st, data = call("POST", f"{pr_url}?api-version={API}", pat, body)
    show(st, data, ["pullRequestId", "status", "mergeStatus", "title", "url"] if st in (200, 201) else None)
    if st not in (200, 201):
        sys.exit("Stopped: PR was not created. Send me the error above.")
    pr_id = data["pullRequestId"]
    web = f"{base}{q(proj)}/_git/{q(repo)}/pullrequest/{pr_id}"
    print(f"Web link: {web}")

    step("6. Create the same PR again (expect 'already exists' error)")
    st, data = call("POST", f"{pr_url}?api-version={API}", pat, body)
    print("EXACT ERROR TEXT BELOW (needed for the app):")
    show(st, data)

    step("7. Turn on auto-complete")
    patch = {"autoCompleteSetBy": {"id": user_id}, "completionOptions": {"deleteSourceBranch": False}}
    st, data = call("PATCH", f"{pr_url}/{pr_id}?api-version={API}", pat, patch)
    show(st, data, ["pullRequestId", "status", "autoCompleteSetBy", "completionOptions"] if st == 200 else None)

    step("8. Clean up")
    if ask("Abandon the test PR now? (y/n)", "y").lower() == "y":
        st, data = call("PATCH", f"{pr_url}/{pr_id}?api-version={API}", pat, {"status": "abandoned"})
        show(st, data, ["pullRequestId", "status"] if st == 200 else None)
    else:
        print(f"Left open: {web}")

    print("\nDone. Send me the output (the PAT is never printed).")


if __name__ == "__main__":
    main()

"""Check out the exact renderer source revision used by this RyGTS release."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess


def run(*args):
    subprocess.run(args, check=True)


def prepare(destination):
    root = Path(__file__).resolve().parent.parent
    source = json.loads((root / "renderer/source.json").read_text(encoding="utf-8"))
    revision = source["commit"]
    expected = os.environ.get("RENDERER_SHA")
    if expected and not re.fullmatch(r"[0-9a-f]{40}", expected):
        raise SystemExit("Invalid expected renderer SHA")
    if revision == "HEAD":
        if expected:
            revision = expected
        else:
            ref = "refs/heads/" + source["branch"]
            result = subprocess.check_output(["git", "ls-remote", "--exit-code", source["repository"], ref], text=True).splitlines()
            if len(result) != 1 or len(result[0].split()) != 2 or result[0].split()[1] != ref:
                raise SystemExit("Cannot resolve renderer branch")
            revision = result[0].split()[0]
    elif expected and expected != revision:
        raise SystemExit("Expected renderer SHA differs from source pin")
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise SystemExit("Renderer source must be HEAD or a full commit SHA")
    destination = Path(destination).resolve()
    if destination.exists():
        raise SystemExit(f"Refusing to overwrite existing source: {destination}")
    run("git", "init", str(destination))
    run("git", "-C", str(destination), "config", "core.longpaths", "true")
    run("git", "-C", str(destination), "remote", "add", "origin", source["repository"])
    run("git", "-C", str(destination), "fetch", "--depth=1", "origin", revision)
    run("git", "-C", str(destination), "checkout", "--detach", "FETCH_HEAD")
    actual = subprocess.check_output(
        ["git", "-C", str(destination), "rev-parse", "HEAD"], text=True).strip()
    if actual != revision:
        raise SystemExit(f"Renderer revision mismatch: {actual}")
    (destination / "CI-SOURCE.txt").write_text(
        source["repository"] + "\n" + revision + "\n", encoding="utf-8")
    run("git", "-C", str(destination), "-c", "core.longpaths=true",
        "submodule", "update", "--init", "--recursive", "--jobs", "4")
    apply_fork_patches(root, destination)


def apply_fork_patches(root, destination):
    """aminenrr/RyGTS-SPS fork: renderer changes live as patches in this repository
    (ci/fork/shadgt-patches/*.patch, applied in name order on the pinned renderer)."""
    folder = root / "ci" / "fork" / "shadgt-patches"
    patches = sorted(folder.glob("*.patch")) if folder.is_dir() else []
    applied = []
    for patch in patches:
        run("git", "-C", str(destination), "apply", "--whitespace=nowarn", "--verbose", str(patch))
        applied.append(patch.name)
    (destination / "FORK-PATCHES.txt").write_text("".join(name + "\n" for name in applied),
                                                   encoding="utf-8")
    print(f"Applied {len(applied)} fork renderer patch(es): {', '.join(applied) or 'none'}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("destination")
    prepare(parser.parse_args().destination)

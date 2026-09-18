#!/usr/bin/env bash
# Canonical: TagShelfCommon/scripts/package.sh
# Vendored into each plugin by sync-into-plugins.sh. Edit here, then sync. Do not edit copies.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
plugin="$(basename "$root")"
csproj="Jellyfin.Plugin.${plugin}.csproj"
if [[ ! -f "$csproj" ]]; then
  echo "package.sh: expected ${csproj} in ${root}" >&2
  exit 1
fi

eval "$(python3 - "$csproj" <<'PY'
import re, shlex, sys
from pathlib import Path

text = Path(sys.argv[1]).read_text()

def tag(name, default=""):
    match = re.search(rf"<{name}>([^<]+)</{name}>", text)
    return match.group(1).strip() if match else default

raw = tag("Version")
if not raw:
    raise SystemExit("package.sh: missing <Version> in csproj")
parts = [p for p in raw.split(".") if p != ""]
while len(parts) < 4:
    parts.append("0")
print(f"version={shlex.quote('.'.join(parts[:4]))}")
print(f"tfm={shlex.quote(tag('TargetFramework', 'net9.0'))}")
print(f"assembly={shlex.quote(tag('AssemblyName', Path(sys.argv[1]).stem))}")
PY
)"

owner="TidBits16"
if origin="$(git remote get-url origin 2>/dev/null || true)"; then
  if [[ "$origin" =~ github.com[:/]([^/]+)/ ]]; then
    owner="${BASH_REMATCH[1]}"
  fi
fi

export PATH="${HOME}/.dotnet:${PATH}"
dotnet build "$csproj" -c Release --nologo

dll="bin/Release/${tfm}/${assembly}.dll"
if [[ ! -f "$dll" ]]; then
  echo "package.sh: missing ${dll}" >&2
  exit 1
fi

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
cp "$dll" "$stage/${assembly}.dll"
cp meta.json "$stage/"
cp backdrop.svg "$stage/"

mkdir -p dist
zip_path="$root/dist/${plugin}_${version}.zip"
rm -f "$zip_path"
source_url="https://github.com/${owner}/${plugin}/releases/download/v${version}/${plugin}_${version}.zip"
timestamp="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"

python3 - "$stage" "$zip_path" "$assembly" "$version" "$timestamp" "$source_url" <<'PY'
import hashlib, json, sys, zipfile
from pathlib import Path

stage, zip_path, assembly, version, timestamp, source_url = sys.argv[1:]
dll_name = f"{assembly}.dll"
names = (dll_name, "meta.json", "backdrop.svg")
with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
    for name in names:
        zf.write(Path(stage) / name, name)

checksum = hashlib.md5(Path(zip_path).read_bytes()).hexdigest()
meta = json.loads(Path("meta.json").read_text())
entry = {
    "guid": meta["guid"],
    "name": meta["name"],
    "description": meta["description"],
    "overview": meta["overview"],
    "owner": meta["owner"],
    "category": meta["category"],
    "imageUrl": meta.get("imageUrl") or "",
    "versions": [],
}

manifest_path = Path("manifest.json")
if manifest_path.exists():
    data = json.loads(manifest_path.read_text())
    if isinstance(data, list) and data:
        entry = data[0]

versions = [v for v in entry.get("versions", []) if v.get("version") != version]
versions.insert(0, {
    "version": version,
    "changelog": meta.get("changelog") or f"Release {version}",
    "targetAbi": meta.get("targetAbi") or "10.11.0.0",
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": timestamp,
})
entry["versions"] = versions
entry["guid"] = meta["guid"]
entry["name"] = meta["name"]
entry["description"] = meta["description"]
entry["overview"] = meta["overview"]
entry["owner"] = meta["owner"]
entry["category"] = meta["category"]
entry["imageUrl"] = meta.get("imageUrl") or ""
manifest_path.write_text(json.dumps([entry], indent=2) + "\n")
print(f"zip {source_url}")
print(f"md5 {checksum}")
PY

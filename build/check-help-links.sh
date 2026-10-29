#!/usr/bin/env bash
# Checks the help pages before a release, so no version ships with a "?" button that opens a missing page.
#
#   build/check-help-links.sh                 files only: fast, no network, run it any time
#   build/check-help-links.sh --remote <ref>  also fetches each page from GitHub at <ref>, as the app will
#
# Files: every page in ChartShared.Core/Help/HelpPages.cs is a tracked file under docs/help, spelled exactly (GitHub's
# addresses are case-sensitive, and Windows isn't), and every relative link between the pages lands on one.
#
# Remote: a release opens its pages at its tag, which doesn't exist until the release is made, so the release workflow checks
# the commit the tag will point at, which holds the same files. The commit must be pushed. The address is built from the
# constants in HelpPages.cs, so it's the one the app opens.
set -euo pipefail
cd "$(dirname "$0")/.."

source_file=ChartShared.Core/Help/HelpPages.cs
folder=docs/help
remote_ref=""
if [[ "${1:-}" == "--remote" ]]; then
  remote_ref="${2:?--remote needs the commit or tag to fetch the pages at.}"
elif [[ $# -gt 0 ]]; then
  echo "Usage: $0 [--remote <ref>]" >&2
  exit 2
fi

failures=0
fail() {
  failures=$((failures + 1))
  if [[ -n "${GITHUB_ACTIONS:-}" ]]; then echo "::error::$1"; else echo "FAIL: $1"; fi
}
warn() {
  if [[ -n "${GITHUB_ACTIONS:-}" ]]; then echo "::warning::$1"; else echo "warn: $1"; fi
}

# The pages the app can open: its string constants.
mapfile -t pages < <(sed -n 's/^[[:space:]]*public const string \([A-Za-z]*\) = "\([^"]*\)";.*/\1 \2/p' "$source_file" \
  | cut -d' ' -f2 | tr -d '\r')
if (( ${#pages[@]} < 20 )); then
  echo "Found only ${#pages[@]} pages in $source_file; its layout must have changed, so fix the pattern in this script." >&2
  exit 1
fi

# What git tracks is what a tag holds, so this is the case-exact, committed list.
mapfile -t tracked < <(git ls-files "$folder" | tr -d '\r')
is_tracked() {
  local file
  for file in "${tracked[@]}"; do
    [[ "$file" == "$1" ]] && return 0
  done
  return 1
}

percent_decode() {
  local rest="$1" out=""
  while [[ "$rest" =~ ^([^%]*)%([0-9A-Fa-f]{2})(.*)$ ]]; do
    out+="${BASH_REMATCH[1]}$(printf "\\x${BASH_REMATCH[2]}")"
    rest="${BASH_REMATCH[3]}"
  done
  printf '%s' "$out$rest"
}

is_page() {
  local page
  for page in "${pages[@]}"; do
    [[ "$page" == "$1" ]] && return 0
  done
  return 1
}

echo "Checking ${#pages[@]} help pages."
for page in "${pages[@]}"; do
  is_tracked "$folder/$page.md" || fail "$folder/$page.md isn't a tracked file, but HelpPages.cs opens it (check its spelling and case, and that it's committed)."
done

# Pages nothing opens, which a person may have left behind when a window was removed. Old releases open theirs at their own tags.
for file in "${tracked[@]}"; do
  name="$(basename "$file" .md)"
  [[ "$file" == *.md ]] || continue
  is_page "$name" || warn "$file isn't opened by any window. Is it still wanted?"
done

# Links between the pages: [text](Other.md) or (Other.md#heading), as the pages are read on GitHub. A link's title
# ([text](Other.md "title")) isn't part of its target, a target may be percent-encoded, and one with a scheme (https:, mailto:)
# or only a #heading isn't a page.
for file in "${tracked[@]}"; do
  [[ "$file" == *.md ]] || continue
  while IFS= read -r inner; do
    read -r target _ <<< "$inner"
    case "$target" in
      "" | \#* | [A-Za-z]*:*) continue ;;
    esac
    path="${target%%#*}"
    path="$(percent_decode "$path")"
    [[ -n "$path" ]] || continue
    resolved="$(realpath -m --relative-to=. "$(dirname "$file")/$path" | tr -d '\r')"
    is_tracked "$resolved" || fail "$file links to $target, but $resolved isn't a tracked file."
  done < <(grep -o '\]([^)]*)' "$file" | sed 's/^](//; s/)$//' | tr -d '\r')
done

if [[ -n "$remote_ref" ]]; then
  blob_url="$(sed -n 's/^[[:space:]]*private const string BlobUrl = "\([^"]*\)";.*/\1/p' "$source_file" | tr -d '\r')"
  folder_path="$(sed -n 's/^[[:space:]]*private const string Folder = "\([^"]*\)";.*/\1/p' "$source_file" | tr -d '\r')"
  [[ -n "$blob_url" && -n "$folder_path" ]] || { echo "Couldn't read BlobUrl and Folder from $source_file." >&2; exit 1; }

  echo "Fetching each page at $remote_ref."
  attempts=3
  for page in "${pages[@]}"; do
    url="${blob_url}${remote_ref}${folder_path}${page}.md"
    # A freshly pushed commit can take a moment to show, so a miss is tried again.
    code=000
    for (( attempt = 1; attempt <= attempts; attempt++ )); do
      code="$(curl -s -o /dev/null -w '%{http_code}' -L --max-time 30 "$url" || true)"
      [[ "$code" == 200 ]] && break
      (( attempt < attempts )) && sleep $((attempt * 3))
    done
    if [[ "$code" != 200 ]]; then
      fail "$url answered $code (is $remote_ref pushed, and is the repository public?)."
      # The commit isn't there at all, most likely, so the rest aren't waited on: each is tried once.
      attempts=1
    fi
  done
fi

if (( failures > 0 )); then
  echo "$failures help link problem(s)."
  exit 1
fi
echo "All help links are good."

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GitManager.Services.Repositories;

namespace GitManager.Services.Remote;

public sealed record RemoteTextReadResult(bool Exists, bool TooLarge, bool IsBinary, bool InvalidUtf8, string Text);

public sealed record RemoteWorkingFileInfo(
    string Path,
    bool Exists,
    bool IsFile,
    bool IsDirectory,
    bool IsLink,
    string? LinkTarget,
    string ContentHash,
    string? Text,
    bool IsBinary,
    bool InvalidUtf8);

public sealed class RemoteFileSystem(RemoteSshRunner ssh)
{
    public async Task<bool> ExistsDirectoryAsync(string root, CancellationToken token = default)
    {
        var (alias, remoteDir) = RemoteRepositoryPath.Parse(root);
        var result = await ssh.RunCommandAsync(alias, $"test -d {RemoteSshRunner.QuotePosix(remoteDir)}",
            TimeSpan.FromSeconds(15), allowFailure: true, cancellationToken: token).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    public async Task<bool> HasAnyGitMarkerAsync(string root, string gitDir, IEnumerable<string> markers, CancellationToken token = default)
    {
        var (alias, _) = RemoteRepositoryPath.Parse(root);
        string script = """
import json, os, sys
req = json.load(sys.stdin)
base = req["gitDir"]
hit = any(os.path.exists(os.path.join(base, name)) for name in req["markers"])
print(json.dumps({"exists": hit}))
""";
        var dto = await RunPythonJsonAsync<MarkerResult>(alias, script, new { gitDir, markers = markers.ToArray() }, TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        return dto?.Exists == true;
    }

    public async Task<HashSet<string>> FilterExistingPathsAsync(string root, IEnumerable<string> relativePaths, CancellationToken token = default)
    {
        var (alias, remoteDir) = RemoteRepositoryPath.Parse(root);
        string[] paths = relativePaths.ToArray();
        if (paths.Length == 0) return [];
        string script = """
import json, os, sys
req = json.load(sys.stdin)
root = req["root"]
existing = [p for p in req["paths"] if os.path.lexists(os.path.join(root, p))]
print(json.dumps({"existing": existing}))
""";
        var dto = await RunPythonJsonAsync<ExistingPathsResult>(alias, script, new { root = remoteDir, paths }, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        return dto?.Existing?.ToHashSet(StringComparer.Ordinal) ?? [];
    }

    public async Task<RemoteTextReadResult> ReadWorkingTextAsync(string root, string relativePath, int maxBytes, CancellationToken token = default)
    {
        var (alias, remoteDir) = RemoteRepositoryPath.Parse(root);
        string script = """
import json, os, sys
req = json.load(sys.stdin)
full = os.path.join(req["root"], req["path"])
if not os.path.isfile(full):
    print(json.dumps({"exists": False}))
    sys.exit(0)
size = os.path.getsize(full)
if size > req["maxBytes"]:
    print(json.dumps({"exists": True, "tooLarge": True}))
    sys.exit(0)
with open(full, "rb") as f:
    data = f.read()
if b"\x00" in data:
    print(json.dumps({"exists": True, "isBinary": True}))
    sys.exit(0)
try:
    text = data.decode("utf-8-sig")
except UnicodeDecodeError:
    print(json.dumps({"exists": True, "invalidUtf8": True}))
    sys.exit(0)
print(json.dumps({"exists": True, "text": text}))
""";
        var dto = await RunPythonJsonAsync<TextReadDto>(alias, script, new { root = remoteDir, path = relativePath, maxBytes }, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        if (dto is null) return new(false, false, false, false, "");
        return new(dto.Exists, dto.TooLarge, dto.IsBinary, dto.InvalidUtf8, dto.Text ?? "");
    }

    public async Task<ReadmeDocument?> ReadReadmeAsync(string root, CancellationToken token = default)
    {
        var (alias, remoteDir) = RemoteRepositoryPath.Parse(root);
        string script = """
import json, os, sys
req = json.load(sys.stdin)
root = req["root"]
max_bytes = req["maxBytes"]
if not os.path.isdir(root):
    print(json.dumps({"found": False}))
    sys.exit(0)
preferred = [".md", ".markdown", "", ".txt", ".rst"]
candidates = []
for name in os.listdir(root):
    full = os.path.join(root, name)
    if not os.path.isfile(full):
        continue
    stem, ext = os.path.splitext(name)
    if stem.lower() != "readme":
        continue
    rank = preferred.index(ext.lower()) if ext.lower() in preferred else len(preferred)
    candidates.append((rank, name, full))
if not candidates:
    print(json.dumps({"found": False}))
    sys.exit(0)
candidates.sort()
_, name, full = candidates[0]
size = os.path.getsize(full)
with open(full, "rb") as f:
    raw = f.read(max_bytes)
text = raw.decode("utf-8-sig", errors="replace")
if size > max_bytes:
    text += "\n\n……（文件过大，仅显示前 512 KB）"
print(json.dumps({"found": True, "path": full, "text": text}))
""";
        try
        {
            var dto = await RunPythonJsonAsync<ReadmeDto>(alias, script, new { root = remoteDir, maxBytes = 512 * 1024 }, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
            if (dto is not { Found: true, Path.Length: > 0 }) return null;
            return new(RemoteRepositoryPath.Format(alias, dto.Path), dto.Text ?? "");
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or TimeoutException)
        {
            AppLog.Write("读取远端仓库说明失败：" + root, ex);
            return null;
        }
    }

    public async Task<Dictionary<string, RemoteWorkingFileInfo>> InspectWorkingFilesAsync(string root,
        IReadOnlyList<(string Path, bool NeedText)> requests, string gitPath, CancellationToken token = default)
    {
        var (alias, remoteDir) = RemoteRepositoryPath.Parse(root);
        if (requests.Count == 0) return [];
        string script = """
import hashlib, json, os, subprocess, sys
req = json.load(sys.stdin)
root = req["root"]
git_path = req["gitPath"]
items = []
for entry in req["files"]:
    rel = entry["path"]
    need_text = bool(entry["needText"])
    full = os.path.join(root, rel)
    info = {
        "path": rel, "exists": False, "isFile": False, "isDirectory": False,
        "isLink": False, "linkTarget": None, "contentHash": "不存在",
        "text": None, "isBinary": False, "invalidUtf8": False
    }
    if os.path.islink(full):
        target = os.readlink(full)
        info["exists"] = True
        info["isLink"] = True
        info["linkTarget"] = target
        info["contentHash"] = "链接:" + target
    elif os.path.isdir(full):
        info["exists"] = True
        info["isDirectory"] = True
        proc = subprocess.run([git_path, "-C", full, "rev-parse", "--verify", "HEAD"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, check=False)
        info["contentHash"] = proc.stdout if proc.returncode == 0 else ""
    elif os.path.isfile(full):
        info["exists"] = True
        info["isFile"] = True
        h = hashlib.sha256()
        with open(full, "rb") as f:
            while True:
                chunk = f.read(65536)
                if not chunk:
                    break
                h.update(chunk)
        info["contentHash"] = h.hexdigest().upper()
        if need_text:
            with open(full, "rb") as f:
                head = f.read(4096)
                if b"\x00" in head:
                    info["isBinary"] = True
                else:
                    rest = f.read()
                    raw = head + rest
                    if b"\x00" in raw:
                        info["isBinary"] = True
                    else:
                        try:
                            info["text"] = raw.decode("utf-8-sig")
                        except UnicodeDecodeError:
                            info["invalidUtf8"] = True
    items.append(info)
print(json.dumps({"items": items}))
""";
        var payload = new
        {
            root = remoteDir,
            gitPath,
            files = requests.Select(item => new { path = item.Path, needText = item.NeedText }).ToArray()
        };
        var dto = await RunPythonJsonAsync<InspectResultDto>(alias, script, payload, TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
        var map = new Dictionary<string, RemoteWorkingFileInfo>(StringComparer.Ordinal);
        if (dto?.Items is null) return map;
        foreach (var item in dto.Items)
        {
            map[item.Path] = new(item.Path, item.Exists, item.IsFile, item.IsDirectory, item.IsLink,
                item.LinkTarget, item.ContentHash ?? "不存在", item.Text, item.IsBinary, item.InvalidUtf8);
        }
        return map;
    }

    public async Task<TimeSpan> ReadQuietDurationAsync(string root, IEnumerable<string> relativePaths, CancellationToken token = default)
    {
        var (alias, remoteDir) = RemoteRepositoryPath.Parse(root);
        string[] paths = relativePaths.ToArray();
        if (paths.Length == 0) return TimeSpan.MaxValue;
        string script = """
import json, os, sys, time
req = json.load(sys.stdin)
root = req["root"]
now = time.time()
latest = 0.0
for rel in req["paths"]:
    full = os.path.join(root, rel)
    cur = full
    while cur:
        try:
            st = os.lstat(cur)
            if st.st_mtime > latest:
                latest = st.st_mtime
            break
        except OSError:
            parent = os.path.dirname(cur)
            if parent == cur:
                break
            cur = parent
age = max(0.0, now - latest) if latest > 0 else -1.0
print(json.dumps({"ageSeconds": age}))
""";
        var dto = await RunPythonJsonAsync<QuietAgeDto>(alias, script, new { root = remoteDir, paths }, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        if (dto is null || dto.AgeSeconds < 0) return TimeSpan.MaxValue;
        return TimeSpan.FromSeconds(dto.AgeSeconds);
    }

    public async Task<IReadOnlyList<string>> DiscoverRepositoriesViaSshAsync(RemoteHostProfile host, CancellationToken token = default)
    {
        string script = """
import json, os, subprocess, sys, time
req = json.load(sys.stdin)
git_path = req["gitPath"]
max_depth = max(0, min(10, int(req["maxDepth"])))
roots = [os.path.expanduser(p) for p in req["roots"] if p]
skip = {".git",".svn",".hg",".vs",".idea","node_modules","bin","obj","publish","artifacts","target","dist","build","DerivedData",".build",".swiftpm",".dart_tool","Pods",".venv","venv","__pycache__",".cache","packages","Library",".Trash","archive"}
visited, found, queue = set(), set(), []
for r in roots:
    if os.path.isdir(r):
        real = os.path.realpath(r)
        if real not in visited:
            visited.add(real)
            queue.append((real, 0))
deadline = time.monotonic() + 12.0
scanned = 0
while queue and time.monotonic() < deadline and scanned < 20000:
    cur, depth = queue.pop(0)
    scanned += 1
    marker = os.path.join(cur, ".git")
    if os.path.isdir(marker) or os.path.isfile(marker):
        proc = subprocess.run([git_path, "-C", cur, "rev-parse", "--show-toplevel"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=5, check=False)
        if proc.returncode == 0 and os.path.realpath(proc.stdout.strip()) == os.path.realpath(cur):
            found.add(os.path.realpath(cur))
    if depth >= max_depth:
        continue
    try:
        with os.scandir(cur) as entries:
            for e in entries:
                if not e.is_dir(follow_symlinks=False):
                    continue
                if e.name in skip or e.name.startswith("."):
                    continue
                if e.path not in visited:
                    visited.add(e.path)
                    queue.append((e.path, depth + 1))
    except OSError:
        continue
print(json.dumps({"repositories": sorted(found)}))
""";
        var payload = new
        {
            gitPath = host.EffectiveGitPath,
            maxDepth = host.MaxDepth,
            roots = host.Roots.Count > 0 ? host.Roots : ["~/iPhone_Project", "~/Mac_Project", "~/Desktop", "~/Documents"]
        };
        var dto = await RunPythonJsonAsync<DiscoverDto>(host.Alias, script, payload, TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        return (dto?.Repositories ?? []).Select(path => RemoteRepositoryPath.Format(host.Alias, path)).ToArray();
    }

    public async Task OpenInFinderAsync(string root, CancellationToken token = default)
    {
        var (alias, remoteDir) = RemoteRepositoryPath.Parse(root);
        await ssh.RunCommandAsync(alias, $"open {RemoteSshRunner.QuotePosix(remoteDir)}",
            TimeSpan.FromSeconds(10), allowFailure: false, cancellationToken: token).ConfigureAwait(false);
    }

    private async Task<T?> RunPythonJsonAsync<T>(string alias, string pythonScript, object inputPayload, TimeSpan timeout, CancellationToken token)
    {
        string encodedScript = Convert.ToBase64String(Encoding.UTF8.GetBytes(pythonScript));
        string remoteCmd = $"/usr/bin/python3 -c {RemoteSshRunner.QuotePosix($"import base64; exec(base64.b64decode('{encodedScript}').decode('utf-8'))")}";
        string stdinJson = JsonSerializer.Serialize(inputPayload);
        var result = await ssh.RunCommandAsync(alias, remoteCmd, timeout, allowFailure: false, cancellationToken: token, standardInput: stdinJson).ConfigureAwait(false);
        string trimmed = result.Output.Trim();
        if (trimmed.Length == 0) return default;
        return JsonSerializer.Deserialize<T>(trimmed);
    }

    private sealed class MarkerResult { [JsonPropertyName("exists")] public bool Exists { get; set; } }
    private sealed class ExistingPathsResult { [JsonPropertyName("existing")] public List<string>? Existing { get; set; } }
    private sealed class TextReadDto
    {
        [JsonPropertyName("exists")] public bool Exists { get; set; }
        [JsonPropertyName("tooLarge")] public bool TooLarge { get; set; }
        [JsonPropertyName("isBinary")] public bool IsBinary { get; set; }
        [JsonPropertyName("invalidUtf8")] public bool InvalidUtf8 { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
    private sealed class ReadmeDto
    {
        [JsonPropertyName("found")] public bool Found { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
    private sealed class InspectResultDto { [JsonPropertyName("items")] public List<InspectItemDto>? Items { get; set; } }
    private sealed class InspectItemDto
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("exists")] public bool Exists { get; set; }
        [JsonPropertyName("isFile")] public bool IsFile { get; set; }
        [JsonPropertyName("isDirectory")] public bool IsDirectory { get; set; }
        [JsonPropertyName("isLink")] public bool IsLink { get; set; }
        [JsonPropertyName("linkTarget")] public string? LinkTarget { get; set; }
        [JsonPropertyName("contentHash")] public string? ContentHash { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("isBinary")] public bool IsBinary { get; set; }
        [JsonPropertyName("invalidUtf8")] public bool InvalidUtf8 { get; set; }
    }
    private sealed class QuietAgeDto { [JsonPropertyName("ageSeconds")] public double AgeSeconds { get; set; } }
    private sealed class DiscoverDto { [JsonPropertyName("repositories")] public List<string>? Repositories { get; set; } }
}

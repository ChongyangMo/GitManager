#!/usr/bin/env python3
"""GitManager Mac 局域网只读发现服务。

仅监听局域网 UDP 发现请求并回报本机身份与已验证的 Git 仓库路径，
不提供任何写接口或远程命令执行能力；所有 Git 读写仍由 Windows 侧通过 SSH 密钥通道执行。
"""

import getpass
import json
import os
import socket
import subprocess
import sys
import threading
import time
from datetime import datetime, timezone

DEFAULT_CONFIG_PATH = os.path.expanduser("~/.config/gitmanager-discovery/config.json")
SKIPPED_DIRS = {
    ".git",
    ".svn",
    ".hg",
    ".vs",
    ".idea",
    "node_modules",
    "bin",
    "obj",
    "publish",
    "artifacts",
    "target",
    "dist",
    "build",
    "DerivedData",
    ".build",
    ".swiftpm",
    ".dart_tool",
    "Pods",
    ".venv",
    "venv",
    "__pycache__",
    ".cache",
    "packages",
    "Library",
    ".Trash",
    "archive",
}


def log(message: str) -> None:
    timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    sys.stdout.write(f"[{timestamp}] {message}\n")
    sys.stdout.flush()


def load_config(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as handle:
        data = json.load(handle)
    return {
        "alias": str(data.get("alias") or "macmini").strip(),
        "label": str(data.get("label") or "Mac").strip(),
        "port": int(data.get("port") or 49527),
        "ssh_port": int(data.get("ssh_port") or 22),
        "git_path": str(data.get("git_path") or "/usr/bin/git").strip(),
        "roots": [os.path.expanduser(str(item)) for item in (data.get("roots") or []) if str(item).strip()],
        "max_depth": max(0, min(10, int(data.get("max_depth") or 4))),
        "max_directories": max(1, min(100000, int(data.get("max_directories") or 20000))),
        "cache_seconds": max(5, int(data.get("cache_seconds") or 15)),
    }


def is_git_marker(directory: str) -> bool:
    marker = os.path.join(directory, ".git")
    return os.path.isdir(marker) or os.path.isfile(marker)


def verify_repository(git_path: str, directory: str) -> str:
    try:
        proc = subprocess.run(
            [git_path, "-C", directory, "rev-parse", "--show-toplevel"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=5,
            check=False,
        )
        if proc.returncode != 0:
            return ""
        resolved = os.path.realpath(proc.stdout.strip())
        if os.path.realpath(directory) != resolved:
            return ""
        return resolved
    except Exception:
        return ""


def scan_repositories(config: dict) -> list:
    found = set()
    visited = set()
    queue = []
    for root in config["roots"]:
        if not os.path.isdir(root):
            continue
        real_root = os.path.realpath(root)
        if real_root not in visited:
            visited.add(real_root)
            queue.append((real_root, 0))

    scanned = 0
    deadline = time.monotonic() + 12.0
    max_depth = config["max_depth"]
    max_dirs = config["max_directories"]
    git_path = config["git_path"]

    while queue:
        if time.monotonic() >= deadline or scanned >= max_dirs:
            break
        current, depth = queue.pop(0)
        scanned += 1
        if is_git_marker(current):
            verified = verify_repository(git_path, current)
            if verified:
                found.add(verified)
            # 找到仓库后仍可按深度检查子目录，但跳过 .git 等常规忽略目录
        if depth >= max_depth:
            continue
        try:
            with os.scandir(current) as entries:
                for entry in entries:
                    if not entry.is_dir(follow_symlinks=False):
                        continue
                    if entry.name in SKIPPED_DIRS or entry.name.startswith("."):
                        continue
                    if len(visited) >= max_dirs:
                        break
                    child = entry.path
                    if child not in visited:
                        visited.add(child)
                        queue.append((child, depth + 1))
        except OSError:
            continue

    return sorted(found)


class RepositoryCache:
    def __init__(self, config_path: str) -> None:
        self._config_path = config_path
        self._lock = threading.Lock()
        self._config = load_config(config_path)
        self._repos: list = []
        self._updated_at = 0.0
        self._scanned_iso = ""

    @property
    def port(self) -> int:
        return self._config["port"]

    def snapshot(self, force: bool = False) -> dict:
        with self._lock:
            now = time.monotonic()
            if force or (now - self._updated_at) >= self._config["cache_seconds"]:
                try:
                    self._config = load_config(self._config_path)
                except Exception as exc:
                    log(f"重新读取配置失败，继续沿用旧配置：{exc}")
                self._repos = scan_repositories(self._config)
                self._updated_at = time.monotonic()
                self._scanned_iso = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
                log(f"仓库扫描完成，共发现 {len(self._repos)} 个仓库")
            return {
                "type": "gitmanager-announce",
                "version": 1,
                "alias": self._config["alias"],
                "label": self._config["label"],
                "hostname": socket.gethostname(),
                "user": getpass.getuser(),
                "sshPort": self._config["ssh_port"],
                "gitPath": self._config["git_path"],
                "repositories": list(self._repos),
                "scannedAt": self._scanned_iso,
            }


def serve(config_path: str) -> None:
    cache = RepositoryCache(config_path)
    cache.snapshot(force=True)

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    if hasattr(socket, "SO_REUSEPORT"):
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
        except OSError:
            pass
    sock.bind(("0.0.0.0", cache.port))
    log(f"GitManager Mac 发现服务已启动，监听 UDP 0.0.0.0:{cache.port}")

    while True:
        try:
            payload, address = sock.recvfrom(4096)
        except OSError as exc:
            log(f"接收发现报文异常：{exc}")
            time.sleep(0.5)
            continue

        try:
            request = json.loads(payload.decode("utf-8"))
        except Exception:
            continue
        if not isinstance(request, dict) or request.get("type") != "gitmanager-discover":
            continue

        force = bool(request.get("refresh", False))
        response = cache.snapshot(force=force)
        if "nonce" in request and isinstance(request["nonce"], str):
            response["nonce"] = request["nonce"][:64]
        try:
            encoded = json.dumps(response, ensure_ascii=False).encode("utf-8")
            sock.sendto(encoded, address)
            log(f"已响应来自 {address[0]}:{address[1]} 的发现请求（仓库数={len(response['repositories'])}）")
        except OSError as exc:
            log(f"发送发现响应失败：{exc}")


if __name__ == "__main__":
    cfg_path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_CONFIG_PATH
    serve(cfg_path)

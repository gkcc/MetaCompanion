from __future__ import annotations

import hashlib
import json
import os
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path
from types import TracebackType
from typing import Any, Mapping


_MAX_RESPONSE_BYTES = 8 * 1024 * 1024


class RustWorkerError(ValueError):
    """A bounded, user-safe failure while controlling the local Rust worker."""


class RustWorkerHttpError(RustWorkerError):
    def __init__(self, status_code: int, error_code: str):
        self.status_code = int(status_code)
        self.error_code = error_code or "http_error"
        super().__init__(
            f"Rust 求解器返回 HTTP {self.status_code}（{self.error_code}）"
        )


def sha256_file(path: str | Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _free_loopback_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def _rust_worker_command(binary: Path, port: int, data_dir: str) -> list[str]:
    return [
        str(binary),
        "serve",
        "--host=127.0.0.1",
        f"--port={port}",
        f"--data-dir={data_dir}",
        "--no-training-log",
    ]


def _safe_error_code(value: Any) -> str:
    if not isinstance(value, str):
        return "http_error"
    candidate = value.strip().lower()
    if not candidate or len(candidate) > 64:
        return "http_error"
    if any(character not in "abcdefghijklmnopqrstuvwxyz0123456789_-" for character in candidate):
        return "http_error"
    return candidate


def _http_json(
    method: str,
    base_url: str,
    endpoint: str,
    token: str,
    payload: Mapping[str, Any] | None = None,
    *,
    timeout: float = 10.0,
) -> Mapping[str, Any]:
    body = None
    headers = {"Authorization": f"Bearer {token}", "Accept": "application/json"}
    if payload is not None:
        body = json.dumps(
            payload, separators=(",", ":"), ensure_ascii=False
        ).encode("utf-8")
        headers["Content-Type"] = "application/json; charset=utf-8"
    request = urllib.request.Request(
        base_url + endpoint,
        data=body,
        headers=headers,
        method=method,
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read(_MAX_RESPONSE_BYTES + 1)
    except urllib.error.HTTPError as exc:
        try:
            error_body = exc.read(_MAX_RESPONSE_BYTES + 1)
            error = json.loads(error_body.decode("utf-8", errors="replace"))
            error_code = _safe_error_code(error.get("error", {}).get("code"))
        except (AttributeError, json.JSONDecodeError, UnicodeDecodeError):
            error_code = "http_error"
        raise RustWorkerHttpError(exc.code, error_code) from exc
    except (OSError, urllib.error.URLError) as exc:
        raise RustWorkerError("无法连接本地 Rust 求解器。") from exc
    if len(raw) > _MAX_RESPONSE_BYTES:
        raise RustWorkerError("Rust 求解器响应超过安全大小限制。")
    try:
        value = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise RustWorkerError("Rust 求解器返回了无效 JSON。") from exc
    if not isinstance(value, Mapping):
        raise RustWorkerError("Rust 求解器响应不是 JSON 对象。")
    return value


class RustWorkerClient:
    """Start one authenticated release worker without exposing its token in argv."""

    def __init__(
        self,
        binary_path: str | Path,
        *,
        startup_timeout_seconds: float = 10.0,
        data_prefix: str = "metacompanion-rust-worker-",
    ) -> None:
        self.binary = Path(binary_path).resolve()
        if not self.binary.is_file():
            raise RustWorkerError("未找到 Rust 求解器程序。")
        if startup_timeout_seconds <= 0:
            raise RustWorkerError("Rust 求解器启动等待时间必须大于 0。")
        self.startup_timeout_seconds = float(startup_timeout_seconds)
        self.data_prefix = data_prefix
        self.token = secrets.token_hex(32)
        self.port = _free_loopback_port()
        self.base_url = f"http://127.0.0.1:{self.port}"
        self._temporary: tempfile.TemporaryDirectory[str] | None = None
        self._process: subprocess.Popen[str] | None = None
        self.health: Mapping[str, Any] | None = None

    @property
    def binary_identity(self) -> dict[str, Any]:
        return {
            "name": self.binary.name,
            "bytes": self.binary.stat().st_size,
            "sha256": sha256_file(self.binary),
        }

    @property
    def command(self) -> list[str]:
        if self._temporary is None:
            raise RustWorkerError("Rust 求解器尚未启动。")
        return _rust_worker_command(self.binary, self.port, self._temporary.name)

    def start(self) -> "RustWorkerClient":
        if self._process is not None:
            return self
        self._temporary = tempfile.TemporaryDirectory(prefix=self.data_prefix)
        environment = os.environ.copy()
        environment["METACOMPANION_SOLVER_TOKEN"] = self.token
        creation_flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        try:
            self._process = subprocess.Popen(
                self.command,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                creationflags=creation_flags,
                env=environment,
            )
            deadline = time.monotonic() + self.startup_timeout_seconds
            while time.monotonic() < deadline:
                if self._process.poll() is not None:
                    raise RustWorkerError("Rust 求解器启动失败。")
                try:
                    health = _http_json(
                        "GET", self.base_url, "/v1/health", self.token, timeout=0.5
                    )
                    if health.get("status") == "ready" and health.get("backend") == "rust":
                        self.health = health
                        return self
                except RustWorkerError:
                    pass
                time.sleep(0.05)
            raise RustWorkerError("Rust 求解器启动超时。")
        except BaseException:
            self.close()
            raise

    def solve(
        self, request: Mapping[str, Any], *, timeout: float = 15.0
    ) -> Mapping[str, Any]:
        if self._process is None or self._process.poll() is not None:
            raise RustWorkerError("Rust 求解器当前未运行。")
        return _http_json(
            "POST",
            self.base_url,
            "/v1/solve",
            self.token,
            request,
            timeout=timeout,
        )

    def close(self) -> None:
        process = self._process
        self._process = None
        if process is not None:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=3)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=3)
            for stream in (process.stdout, process.stderr):
                if stream is not None:
                    stream.close()
        temporary = self._temporary
        self._temporary = None
        if temporary is not None:
            temporary.cleanup()

    def __enter__(self) -> "RustWorkerClient":
        return self.start()

    def __exit__(
        self,
        exc_type: type[BaseException] | None,
        exc_value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        self.close()

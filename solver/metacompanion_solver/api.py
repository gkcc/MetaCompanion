from __future__ import annotations

import hmac
import json
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Mapping

from . import API_VERSION
from .behavior import BehaviorCorpusError, BehaviorValidationError
from .errors import (
    DuplicateRequestError,
    ResultObservationConflictError,
    SchemaError,
    SolverError,
)
from .schemas import Observation, SolveRequest
from .service import SolverService


class SolverHttpServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address: tuple[str, int], service: SolverService, session_token: str, max_bytes: int):
        super().__init__(address, SolverRequestHandler)
        self.service = service
        self.session_token = session_token
        self.max_bytes = max_bytes


class SolverRequestHandler(BaseHTTPRequestHandler):
    server: SolverHttpServer
    protocol_version = "HTTP/1.1"

    def log_message(self, format: str, *args: Any) -> None:
        # Do not echo requests or tokens into HDT/user logs. The CLI prints lifecycle only.
        return

    def _authorized(self) -> bool:
        authorization = self.headers.get("Authorization", "")
        bearer = authorization[7:].strip() if authorization.lower().startswith("bearer ") else ""
        supplied = (
            bearer
            or self.headers.get("X-Advisor-Token", "")
            or self.headers.get("X-MetaCompanion-Token", "")
        )
        return bool(supplied) and hmac.compare_digest(supplied, self.server.session_token)

    def _send(self, status: HTTPStatus | int, payload: Mapping[str, Any]) -> None:
        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(int(status))
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(body)

    def _error(self, status: HTTPStatus, code: str, message: str, path: str = "") -> None:
        self._send(
            status,
            {
                "api_version": API_VERSION,
                "error": {"code": code, "message": message, "path": path},
            },
        )

    def _check_auth(self) -> bool:
        if self._authorized():
            return True
        self._error(HTTPStatus.UNAUTHORIZED, "unauthorized", "A valid session token is required.")
        return False

    def _read_json(self) -> Mapping[str, Any] | None:
        length_raw = self.headers.get("Content-Length")
        if length_raw is None:
            self._error(HTTPStatus.LENGTH_REQUIRED, "length_required", "Content-Length is required.")
            return None
        try:
            length = int(length_raw)
        except ValueError:
            self._error(HTTPStatus.BAD_REQUEST, "invalid_length", "Content-Length must be an integer.")
            return None
        if length < 0 or length > self.server.max_bytes:
            self._error(HTTPStatus.REQUEST_ENTITY_TOO_LARGE, "request_too_large", "Request body is too large.")
            return None
        try:
            payload = json.loads(self.rfile.read(length).decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            self._error(HTTPStatus.BAD_REQUEST, "invalid_json", str(exc))
            return None
        if not isinstance(payload, dict):
            self._error(HTTPStatus.BAD_REQUEST, "invalid_request", "JSON root must be an object.")
            return None
        return payload

    def do_GET(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
        if not self._check_auth():
            return
        if self.path == "/v1/health":
            self._send(HTTPStatus.OK, self.server.service.health())
            return
        self._error(HTTPStatus.NOT_FOUND, "not_found", "Unknown endpoint.")

    def do_POST(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
        if not self._check_auth():
            return
        payload = self._read_json()
        if payload is None:
            return
        try:
            if self.path == "/v1/solve":
                request = SolveRequest.from_dict(payload)
                result = self.server.service.solve(request)
                self._send(HTTPStatus.OK, result.to_dict())
                return
            if self.path == "/v1/cancel":
                version = payload.get("api_version", API_VERSION)
                if version != API_VERSION:
                    raise SchemaError("request.api_version", f"expected {API_VERSION!r}")
                request_id = payload.get("request_id")
                state_id = payload.get("state_id")
                if request_id is not None and not isinstance(request_id, str):
                    raise SchemaError("request.request_id", "must be a string")
                if state_id is not None and not isinstance(state_id, str):
                    raise SchemaError("request.state_id", "must be a string")
                self._send(HTTPStatus.OK, self.server.service.cancel(request_id, state_id))
                return
            if self.path == "/v1/observe":
                observation = Observation.from_dict(payload)
                self._send(HTTPStatus.OK, self.server.service.observe(observation))
                return
            if self.path == "/v1/behavior":
                self._send(HTTPStatus.OK, self.server.service.append_behavior(dict(payload)))
                return
            self._error(HTTPStatus.NOT_FOUND, "not_found", "Unknown endpoint.")
        except BehaviorValidationError as exc:
            self._error(HTTPStatus.BAD_REQUEST, "schema_error", exc.code, exc.path)
        except BehaviorCorpusError:
            self._error(
                HTTPStatus.INTERNAL_SERVER_ERROR,
                "behavior_log_error",
                "Behavior corpus append failed.",
            )
        except SchemaError as exc:
            self._error(HTTPStatus.BAD_REQUEST, "schema_error", exc.message, exc.path)
        except DuplicateRequestError as exc:
            self._error(HTTPStatus.CONFLICT, "duplicate_request", str(exc))
        except ResultObservationConflictError as exc:
            self._error(
                HTTPStatus.CONFLICT,
                "result_observation_conflict",
                str(exc),
                "request.result",
            )
        except SolverError as exc:
            self._error(HTTPStatus.UNPROCESSABLE_ENTITY, "solver_error", str(exc))
        except Exception:
            # Unexpected details stay server-side; this API must not leak process data.
            self._error(HTTPStatus.INTERNAL_SERVER_ERROR, "internal_error", "Unexpected solver failure.")


def create_server(
    service: SolverService,
    session_token: str,
    host: str = "127.0.0.1",
    port: int = 17853,
    max_request_bytes: int = 2 * 1024 * 1024,
) -> SolverHttpServer:
    if host != "127.0.0.1":
        raise ValueError("solver HTTP server may only bind to 127.0.0.1")
    if not session_token or len(session_token) < 16:
        raise ValueError("session token must contain at least 16 characters")
    return SolverHttpServer((host, port), service, session_token, max_request_bytes)

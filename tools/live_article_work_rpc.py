#!/usr/bin/env python3
"""Live Article Work RPC test against a running BackFiller.

Connects to the existing TLS NNTP listener, publishes one Article Work v1
request, and issues ``ARTICLE <message-id>`` on that same NNTP connection.
It does not start, stop, or reconfigure the server, and it does not declare,
purge, or delete the ``backfiller.{backbone}`` topology.

The reply queue is armed before the NNTP greeting so the one-second response
TTL cannot expire before this process is listening. The request itself is
published only after the greeting has been validated, and it is published
once.

Usage and environment variables are in ``tools/live_article_work_rpc.md``.
Run ``python tools/live_article_work_rpc.py --help`` for the flags.
``--self-test`` checks this script's framing and schema helpers locally and
does not open a socket.
"""

from __future__ import annotations

import argparse
import contextlib
import ipaddress
import json
import math
import os
import re
import socket
import ssl
import sys
import time
import uuid
from dataclasses import dataclass

# Article Work v1. Property names match ArticleWorkRequestParser and
# ArticleWorkResponseWireProtocol. AMQP CorrelationId and ReplyTo are properties,
# not JSON fields. The RequestId header is required to match JSON requestId when
# the header is present.
PROTOCOL_VERSION = 1
JSON_CONTENT_TYPE = "application/json"
REQUEST_EXPIRATION_MILLISECONDS = "1000"
WORK_REQUEST_MAX_PAYLOAD_BYTES = 1024
TOPOLOGY_PREFIX = "backfiller"
PROVIDER_BACKBONES = (
    "Abavia",
    "Altopia",
    "BaseIP",
    "Eweka",
    "Elbracht",
    "Giganews",
    "GTT",
    "Highwinds",
    "ItsHosted",
    "Novia",
    "UExpress",
    "UsenetNode1",
)

GREETING = b"201 NNTP Service Ready, posting prohibited\r\n"
ARTICLE_FOLLOWS_SUFFIX = b" article follows\r\n"
TERMINATOR = b".\r\n"
QUIT_COMMAND = b"QUIT\r\n"
MAX_STATUS_LINE_OCTETS = 512
# Retained lines are at most 1024 octets. The listener adds at most one header
# stuffing dot, and body lines are already stuffed once, so a wire line can be
# one octet longer than the retained line.
MAX_WIRE_LINE_CONTENT = 1025
DEFAULT_MAX_ARTICLE_BYTES = 64 * 1024 * 1024
DEFAULT_RPC_TIMEOUT_SECONDS = 30.0
DEFAULT_CONNECT_TIMEOUT_SECONDS = 30.0
DEFAULT_NNTP_TIMEOUT_SECONDS = 60.0
DEFAULT_RABBITMQ_PORT = 5672
DEFAULT_RABBITMQ_VHOST = "/"
CONNECTION_NAME = "backfiller-live-article-work-rpc"

ATEXT = frozenset(
    b"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$%&'*+-/=?^_`{|}~"
)
_SECRET_URL = re.compile(r"(?i)(amqps?://)[^/\s]+@")
_BACKBONE_TOKEN = re.compile(r"[A-Za-z0-9._-]{1,64}\Z")
_ARTICLE_ID_HEX = re.compile(r"[0-9a-f]{64}\Z")


class ConfigError(Exception):
    """Raised when arguments or environment variables are unusable."""


class TestFailure(Exception):
    """One failed live assertion. ``category`` is the operator-facing class."""

    def __init__(
        self,
        category: str,
        detail: str,
        *,
        rpc_elapsed: float | None = None,
        article_elapsed: float | None = None,
        article_bytes: int | None = None,
    ) -> None:
        super().__init__(detail)
        self.category = category
        self.detail = detail
        self.rpc_elapsed = rpc_elapsed
        self.article_elapsed = article_elapsed
        self.article_bytes = article_bytes


@dataclass(frozen=True)
class Config:
    """Resolved settings. String fields that hold secrets are never formatted."""

    nntp_host: str
    nntp_port: int
    tls_server_name: str | None
    ca_file: str | None
    insecure_tls: bool
    amqp_url: str | None
    rabbit_host: str | None
    rabbit_port: int
    rabbit_vhost: str
    rabbit_user: str | None
    rabbit_password: str | None
    rabbit_tls: bool
    rabbit_ca_file: str | None
    backbone: str
    message_id: bytes
    message_id_text: str
    connect_timeout: float
    rpc_timeout: float
    nntp_timeout: float
    max_article_bytes: int

    def secrets(self) -> list[str]:
        values: list[str] = []
        for secret in (self.rabbit_password, self.rabbit_user, self.amqp_url):
            if secret:
                values.append(secret)
        return values

    @property
    def exchange(self) -> str:
        return f"{TOPOLOGY_PREFIX}.{self.backbone.lower()}"


@dataclass(frozen=True)
class RpcSuccess:
    """Schema-valid Article Work success body."""

    request_id: uuid.UUID
    message_id: str
    backbone: str
    fqdn: str
    listener_port: int
    article_id: str


class NntpIoError(Exception):
    """Socket failure while reading an NNTP line. ``kind`` is timeout, disconnect, or framing."""

    def __init__(self, kind: str, detail: str) -> None:
        super().__init__(detail)
        self.kind = kind
        self.detail = detail


class NntpSession:
    """One TLS NNTP session. Buffered reads accept partial and coalesced TCP segments."""

    def __init__(self, sock: socket.socket, max_line_content: int) -> None:
        self._sock = sock
        self._buffer = bytearray()
        self._max_line_content = max_line_content
        self.greeting_ok = False
        self.article_started = False
        self.article_complete = False

    def read_line(self) -> bytes:
        """Return one CRLF-terminated line, including the CRLF."""
        limit = self._max_line_content
        while True:
            crlf = self._buffer.find(b"\r\n")
            if crlf >= 0:
                if crlf > limit:
                    raise NntpIoError("framing", "NNTP line exceeded the maximum length.")
                end = crlf + 2
                line = bytes(self._buffer[:end])
                del self._buffer[:end]
                return line
            if len(self._buffer) > limit + 1:
                raise NntpIoError("framing", "NNTP line exceeded the maximum length.")
            self._fill()

    def read_multiline(self, max_wire_bytes: int) -> bytes:
        """Read a multiline block, undo one stuffing dot per content line, and drop the terminator.

        A wire line of ``.\\r\\n`` is content that destuffs to ``.\\r\\n``. Only a line whose
        first octet is ``.`` and whose remaining octets are exactly ``\\r\\n`` ends the block.
        Line endings are preserved. The returned bytes do not include the terminator.
        """
        article = bytearray()
        wire = 0
        while True:
            line = self.read_line()
            wire += len(line)
            if wire > max_wire_bytes:
                raise NntpIoError(
                    "framing",
                    "NNTP multiline response exceeded the configured maximum size.",
                )
            if line == TERMINATOR:
                return bytes(article)
            if line.startswith(b"."):
                line = line[1:]
            article.extend(line)

    def send(self, payload: bytes) -> None:
        view = memoryview(payload)
        sent = 0
        while sent < len(view):
            try:
                wrote = self._sock.send(view[sent:])
            except TimeoutError as exc:
                raise NntpIoError("timeout", "Timed out writing to the NNTP listener.") from exc
            except OSError as exc:
                raise NntpIoError("disconnect", "NNTP connection closed while writing.") from exc
            if wrote <= 0:
                raise NntpIoError("disconnect", "NNTP connection closed while writing.")
            sent += wrote

    def close(self) -> None:
        # Closing an already-closed socket raises OSError. That must not fail the test.
        with contextlib.suppress(OSError):
            self._sock.close()

    def _fill(self) -> None:
        try:
            chunk = self._sock.recv(65536)
        except TimeoutError as exc:
            raise NntpIoError("timeout", "Timed out reading from the NNTP listener.") from exc
        except OSError as exc:
            raise NntpIoError("disconnect", "NNTP connection closed while reading.") from exc
        if not chunk:
            if self._buffer:
                raise NntpIoError("disconnect", "NNTP connection closed before a CRLF.")
            raise NntpIoError("disconnect", "NNTP connection closed.")
        self._buffer.extend(chunk)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if args.self_test:
        return run_self_test()
    try:
        config = build_config(args)
    except ConfigError as exc:
        print(f"[FAIL] {exc}")
        print()
        print("Result: FAIL")
        print("Failure: invalid configuration")
        return 2
    try:
        import_pika()
    except ConfigError as exc:
        print(f"[FAIL] {exc}")
        print()
        print("Result: FAIL")
        print("Failure: missing dependency")
        return 2
    try:
        run_live(config)
    except TestFailure as exc:
        print(f"[FAIL] {exc.detail}")
        print()
        print("Result: FAIL")
        print(f"Failure: {exc.category}")
        _print_measurements(exc.rpc_elapsed, exc.article_elapsed, exc.article_bytes)
        return 1
    except Exception as exc:
        print(f"[FAIL] {redact(str(exc), config.secrets())}")
        print()
        print("Result: FAIL")
        print("Failure: unexpected client error")
        return 1
    return 0


def parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Live test: TLS NNTP greeting, one Article Work v1 RabbitMQ RPC, "
            "then ARTICLE <message-id> on the same NNTP connection."
        ),
    )
    parser.add_argument("--self-test", action="store_true", help="Run local framing, schema, and configuration checks. No network.")
    parser.add_argument("--nntp-host", default=os.environ.get("NNTP_HOST"), help="Listener host. Env: NNTP_HOST.")
    parser.add_argument("--nntp-port", default=os.environ.get("NNTP_PORT"), help="TLS listener port (BackFiller:BindPortTls). Env: NNTP_PORT.")
    parser.add_argument(
        "--tls-server-name",
        default=os.environ.get("NNTP_TLS_SERVER_NAME"),
        help="SNI and certificate name when it differs from --nntp-host. Env: NNTP_TLS_SERVER_NAME.",
    )
    parser.add_argument("--ca-file", default=os.environ.get("NNTP_CA_FILE"), help="PEM CA bundle for the listener certificate. Env: NNTP_CA_FILE.")
    parser.add_argument(
        "--insecure-skip-tls-verify",
        action=argparse.BooleanOptionalAction,
        default=None,
        help=(
            "Disable NNTP certificate and hostname checks. "
            "--no-insecure-skip-tls-verify forces verification. "
            "Omitted: NNTP_INSECURE_SKIP_TLS_VERIFY, default false."
        ),
    )
    parser.add_argument("--message-id", default=os.environ.get("BACKFILLER_MESSAGE_ID"), help="Message-ID including angle brackets. Env: BACKFILLER_MESSAGE_ID.")
    parser.add_argument("--backbone", default=os.environ.get("BACKFILLER_BACKBONE"), help="Article Work backbone label, for example Giganews. Env: BACKFILLER_BACKBONE.")
    parser.add_argument(
        "--amqp-url",
        default=None,
        help="AMQP URL. Overrides discrete RabbitMQ settings. Not logged. Env: BACKFILLER_AMQP_URL.",
    )
    parser.add_argument("--rabbitmq-host", default=None, help="Broker host. Env: RABBITMQ_HOST. Overrides BACKFILLER_AMQP_URL.")
    parser.add_argument(
        "--rabbitmq-port",
        default=None,
        help="Broker TCP port. Default 5672. Env: RABBITMQ_PORT. Ignored when an AMQP URL is used.",
    )
    parser.add_argument(
        "--rabbitmq-vhost",
        default=None,
        help="Virtual host. Default /. Env: RABBITMQ_VHOST. Ignored when an AMQP URL is used.",
    )
    parser.add_argument(
        "--rabbitmq-username",
        default=None,
        help="Broker username. Env: VECTOR__RABBITMQ__USERNAME. Not logged. Ignored when an AMQP URL is used.",
    )
    parser.add_argument(
        "--rabbitmq-password",
        default=None,
        help="Broker password. Prefer VECTOR__RABBITMQ__PASSWORD so it is not on the command line. Not logged. Ignored when an AMQP URL is used.",
    )
    parser.add_argument(
        "--rabbitmq-tls",
        action=argparse.BooleanOptionalAction,
        default=None,
        help=(
            "Use TLS for discrete AMQP settings. --no-rabbitmq-tls forces plaintext. "
            "Omitted: RABBITMQ_USE_TLS, default false. Ignored when an AMQP URL is used."
        ),
    )
    parser.add_argument(
        "--rabbitmq-ca-file",
        default=None,
        help="PEM CA bundle for the broker. Env: RABBITMQ_CA_FILE. Ignored when an AMQP URL is used.",
    )
    parser.add_argument("--connect-timeout", default=os.environ.get("BACKFILLER_CONNECT_TIMEOUT_SECONDS", str(DEFAULT_CONNECT_TIMEOUT_SECONDS)), help="TCP and TLS handshake timeout in seconds. Default 30.")
    parser.add_argument("--rpc-timeout", default=os.environ.get("BACKFILLER_RPC_TIMEOUT_SECONDS", str(DEFAULT_RPC_TIMEOUT_SECONDS)), help="Seconds to wait for the matching RPC response after publish. Default 30.")
    parser.add_argument("--nntp-timeout", default=os.environ.get("BACKFILLER_NNTP_TIMEOUT_SECONDS", str(DEFAULT_NNTP_TIMEOUT_SECONDS)), help="Per-read and per-write NNTP timeout in seconds. Default 60.")
    parser.add_argument("--max-article-bytes", default=os.environ.get("BACKFILLER_MAX_ARTICLE_BYTES", str(DEFAULT_MAX_ARTICLE_BYTES)), help="Maximum wire bytes of the multiline ARTICLE body, including the terminator. Default 67108864.")
    return parser.parse_args(argv)


def _blank_to_none(value: object) -> str | None:
    if isinstance(value, str) and value.strip():
        return value.strip()
    return None


def _flag_or_env_bool(flag: object, env_name: str, default: bool) -> bool:
    """Use an explicit CLI bool. None means the flag was omitted, so the env var or default applies."""
    if isinstance(flag, bool):
        return flag
    raw = os.environ.get(env_name)
    if raw is None or raw.strip() == "":
        return default
    return _env_bool(env_name)


def _first_text(flag: object, env_name: str) -> str | None:
    text = _blank_to_none(flag)
    if text is not None:
        return text
    return _blank_to_none(os.environ.get(env_name))


def build_config(args: argparse.Namespace) -> Config:
    nntp_host = _required_text(args.nntp_host, "NNTP host (--nntp-host or NNTP_HOST)")
    nntp_port = _port(args.nntp_port, "NNTP port")
    backbone = _required_text(args.backbone, "backbone (--backbone or BACKFILLER_BACKBONE)")
    if _BACKBONE_TOKEN.fullmatch(backbone) is None:
        raise ConfigError("backbone must be 1–64 characters from A-Z, a-z, 0-9, '.', '_', or '-'.")
    message_id = validate_message_id(_required_text(args.message_id, "Message-ID (--message-id or BACKFILLER_MESSAGE_ID)"))
    cli_url = _blank_to_none(args.amqp_url)
    cli_host = _blank_to_none(args.rabbitmq_host)
    if cli_url and cli_host:
        raise ConfigError("Pass either --amqp-url or --rabbitmq-host, not both.")
    if cli_url is not None:
        amqp_url: str | None = cli_url
        rabbit_host: str | None = None
    elif cli_host is not None:
        # An explicit host flag overrides an environment AMQP URL.
        amqp_url = None
        rabbit_host = cli_host
    else:
        amqp_url = _blank_to_none(os.environ.get("BACKFILLER_AMQP_URL"))
        rabbit_host = None if amqp_url is not None else _blank_to_none(os.environ.get("RABBITMQ_HOST"))
    if amqp_url is None:
        if rabbit_host is None:
            raise ConfigError("RabbitMQ host is required (--rabbitmq-host or RABBITMQ_HOST), or pass --amqp-url.")
        rabbit_user = _first_text(args.rabbitmq_username, "VECTOR__RABBITMQ__USERNAME")
        if rabbit_user is None:
            raise ConfigError("RabbitMQ username is required (VECTOR__RABBITMQ__USERNAME or --rabbitmq-username).")
        rabbit_password = args.rabbitmq_password
        if rabbit_password is None:
            rabbit_password = os.environ.get("VECTOR__RABBITMQ__PASSWORD")
        if rabbit_password is None or rabbit_password == "":
            raise ConfigError("RabbitMQ password is required (VECTOR__RABBITMQ__PASSWORD or --rabbitmq-password).")
        port_text = args.rabbitmq_port
        if port_text is None:
            port_text = os.environ.get("RABBITMQ_PORT", str(DEFAULT_RABBITMQ_PORT))
        rabbit_port = _port(port_text, "RabbitMQ port")
        vhost = _first_text(args.rabbitmq_vhost, "RABBITMQ_VHOST") or DEFAULT_RABBITMQ_VHOST
        rabbit_tls = _flag_or_env_bool(args.rabbitmq_tls, "RABBITMQ_USE_TLS", False)
        ca_text = args.rabbitmq_ca_file
        if ca_text is None:
            ca_text = os.environ.get("RABBITMQ_CA_FILE")
        rabbit_ca_file = _optional_path(ca_text)
    else:
        # The URL is the broker identity. Discrete host, port, credentials, TLS, and CA are not read.
        rabbit_host = None
        rabbit_port = DEFAULT_RABBITMQ_PORT
        vhost = DEFAULT_RABBITMQ_VHOST
        rabbit_user = None
        rabbit_password = None
        rabbit_tls = False
        rabbit_ca_file = None
    tls_name = args.tls_server_name.strip() if isinstance(args.tls_server_name, str) and args.tls_server_name.strip() else None
    return Config(
        nntp_host=nntp_host,
        nntp_port=nntp_port,
        tls_server_name=tls_name,
        ca_file=_optional_path(args.ca_file),
        insecure_tls=_flag_or_env_bool(args.insecure_skip_tls_verify, "NNTP_INSECURE_SKIP_TLS_VERIFY", False),
        amqp_url=amqp_url,
        rabbit_host=rabbit_host,
        rabbit_port=rabbit_port,
        rabbit_vhost=vhost,
        rabbit_user=rabbit_user,
        rabbit_password=rabbit_password,
        rabbit_tls=rabbit_tls,
        rabbit_ca_file=rabbit_ca_file,
        backbone=backbone,
        message_id=message_id,
        message_id_text=message_id.decode("ascii"),
        connect_timeout=_positive_float(args.connect_timeout, "connect timeout"),
        rpc_timeout=_positive_float(args.rpc_timeout, "RPC timeout"),
        nntp_timeout=_positive_float(args.nntp_timeout, "NNTP timeout"),
        max_article_bytes=_positive_int(args.max_article_bytes, "max article bytes"),
    )


def run_live(config: Config) -> None:
    """Execute the live sequence. Raises ``TestFailure`` for an assertion miss."""
    rabbit: RabbitSession | None = None
    nntp: NntpSession | None = None
    rpc_elapsed: float | None = None
    article_elapsed: float | None = None
    article_bytes: int | None = None
    try:
        rabbit = connect_rabbit(config)
        nntp = connect_nntp(config)
        print("[PASS] Connected to NNTP listener")
        read_greeting(nntp)
        nntp.greeting_ok = True
        print("[PASS] NNTP greeting validated")
        success, rpc_elapsed = publish_and_wait(rabbit, config)
        print(
            f"[PASS] RPC outcome is Success for {success.fqdn}:{success.listener_port}"
        )
        nntp.article_started = True
        article, article_elapsed = retrieve_article(nntp, config)
        nntp.article_complete = True
        article_bytes = len(article)
        print("[PASS] ARTICLE returned expected success status")
        print("[PASS] Article terminator validated")
        validate_article(article, config, success)
        print("[PASS] Article framing validated")
        print("[PASS] Message-ID matches requested article")
        _report_article_id(config, success)
        _report_endpoint(config, success)
        print()
        print("Result: PASS")
        _print_measurements(rpc_elapsed, article_elapsed, article_bytes)
        print(f"RPC requestId: {success.request_id}")
        print(f"RPC listener: {success.fqdn}:{success.listener_port}")
    except TestFailure as exc:
        raise TestFailure(
            exc.category,
            exc.detail,
            rpc_elapsed=exc.rpc_elapsed if exc.rpc_elapsed is not None else rpc_elapsed,
            article_elapsed=exc.article_elapsed if exc.article_elapsed is not None else article_elapsed,
            article_bytes=exc.article_bytes if exc.article_bytes is not None else article_bytes,
        ) from None
    except Exception as exc:
        raise TestFailure(
            "unexpected client error",
            redact(str(exc), config.secrets()),
            rpc_elapsed=rpc_elapsed,
            article_elapsed=article_elapsed,
            article_bytes=article_bytes,
        ) from None
    finally:
        if nntp is not None:
            if nntp.greeting_ok and (not nntp.article_started or nntp.article_complete):
                _send_quit(nntp)
            nntp.close()
        if rabbit is not None:
            rabbit.close()


def connect_nntp(config: Config) -> NntpSession:
    server_name = config.tls_server_name or config.nntp_host
    try:
        raw = socket.create_connection((config.nntp_host, config.nntp_port), timeout=config.connect_timeout)
    except TimeoutError as exc:
        raise TestFailure("NNTP connection/greeting failure", "Timed out connecting to the NNTP listener.") from exc
    except OSError as exc:
        raise TestFailure("NNTP connection/greeting failure", f"Could not connect to the NNTP listener: {exc}") from exc
    try:
        raw.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        if config.insecure_tls:
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
        else:
            context.check_hostname = True
            context.verify_mode = ssl.CERT_REQUIRED
            if config.ca_file:
                context.load_verify_locations(cafile=config.ca_file)
            else:
                context.load_default_certs()
        tls = context.wrap_socket(raw, server_hostname=None if _is_ip(server_name) and config.insecure_tls else server_name)
        tls.settimeout(config.nntp_timeout)
    except Exception as exc:
        raw.close()
        raise TestFailure("NNTP connection/greeting failure", f"NNTP TLS handshake failed: {exc}") from None
    try:
        return NntpSession(tls, MAX_STATUS_LINE_OCTETS)
    except Exception:
        tls.close()
        raise


def read_greeting(session: NntpSession) -> None:
    session._max_line_content = MAX_STATUS_LINE_OCTETS - 2
    try:
        line = session.read_line()
    except NntpIoError as exc:
        raise TestFailure("NNTP connection/greeting failure", exc.detail) from None
    finally:
        session._max_line_content = MAX_WIRE_LINE_CONTENT
    if line != GREETING:
        raise TestFailure(
            "NNTP connection/greeting failure",
            f"Unexpected NNTP greeting: {_preview(line)}",
        )


def retrieve_article(session: NntpSession, config: Config) -> tuple[bytes, float]:
    command = b"ARTICLE " + config.message_id + b"\r\n"
    if len(command) > MAX_STATUS_LINE_OCTETS:
        raise TestFailure(
            "RPC success followed by NNTP ARTICLE failure",
            "ARTICLE command would exceed the listener's 512-octet command limit.",
            article_elapsed=0.0,
        )
    session._max_line_content = MAX_STATUS_LINE_OCTETS - 2
    # The TLS session is already up. This clock starts at the ARTICLE write and
    # stops when the terminating dot line has been read. Validation is later.
    started = time.perf_counter()
    try:
        session.send(command)
        status = session.read_line()
    except NntpIoError as exc:
        elapsed = time.perf_counter() - started
        category = "Incomplete or malformed article framing" if exc.kind == "framing" else "RPC success followed by NNTP ARTICLE failure"
        hint = _idle_hint(config, None)
        raise TestFailure(category, exc.detail + hint, article_elapsed=elapsed) from None
    expected = b"220 0 " + config.message_id + ARTICLE_FOLLOWS_SUFFIX
    if status != expected:
        elapsed = time.perf_counter() - started
        raise TestFailure(
            "RPC success followed by NNTP ARTICLE failure",
            f"Unexpected ARTICLE status: {_preview(status)}",
            article_elapsed=elapsed,
        )
    session._max_line_content = MAX_WIRE_LINE_CONTENT
    try:
        article = session.read_multiline(config.max_article_bytes)
    except NntpIoError as exc:
        elapsed = time.perf_counter() - started
        raise TestFailure(
            "Incomplete or malformed article framing",
            exc.detail,
            article_elapsed=elapsed,
        ) from None
    return article, time.perf_counter() - started


def validate_article(article: bytes, config: Config, success: RpcSuccess) -> None:
    if not article:
        raise TestFailure("Incomplete or malformed article framing", "ARTICLE body was empty.")
    if b"\x00" in article:
        raise TestFailure("Incomplete or malformed article framing", "Article contains NUL.")
    if not article.endswith(b"\r\n"):
        raise TestFailure("Incomplete or malformed article framing", "Article does not end in CRLF.")
    _require_crlf_lines(article)
    if b"\r\n\r\n" not in article:
        raise TestFailure(
            "Incomplete or malformed article framing",
            "Article has no CRLF CRLF header/body separator.",
        )
    header = _message_id_header(article)
    if header is None:
        raise TestFailure(
            "Incomplete or malformed article framing",
            "Article does not contain exactly one single-line Message-ID header.",
        )
    if header != config.message_id or success.message_id.encode("ascii") != config.message_id:
        raise TestFailure(
            "Message-ID mismatch",
            "Article Message-ID header does not equal the requested Message-ID.",
        )


def _report_article_id(config: Config, success: RpcSuccess) -> None:
    digest = _blake3_hex(config.message_id)
    if digest is None:
        print("[PASS] articleId is 64 lowercase hexadecimal characters")
        print("[NOTE] BLAKE3 package is not installed, so articleId was not recomputed from the Message-ID.")
        return
    if digest != success.article_id:
        raise TestFailure(
            "Message-ID mismatch",
            "RPC articleId does not equal BLAKE3 of the requested Message-ID octets.",
        )
    print("[PASS] articleId matches BLAKE3 of the Message-ID")


def _report_endpoint(config: Config, success: RpcSuccess) -> None:
    if success.listener_port != config.nntp_port:
        raise TestFailure(
            "RPC listener metadata does not match the NNTP connection",
            f"RPC listenerPort {success.listener_port} does not match connected NNTP port {config.nntp_port}.",
        )
    print("[PASS] RPC listenerPort matches the NNTP port")
    identity = config.tls_server_name or config.nntp_host
    if _is_ip(identity) or identity.lower() == "localhost":
        print("[PASS] RPC fqdn is a canonical listener name")
        return
    if identity.rstrip(".").lower() != success.fqdn:
        raise TestFailure(
            "RPC listener metadata does not match the NNTP connection",
            f"RPC fqdn {success.fqdn} does not match the NNTP server name.",
        )
    print("[PASS] RPC fqdn matches the NNTP server name")


def _require_crlf_lines(article: bytes) -> None:
    index = 0
    length = len(article)
    while index < length:
        if article[index] == 0x0D:
            if index + 1 >= length or article[index + 1] != 0x0A:
                raise TestFailure("Incomplete or malformed article framing", "Article contains a bare CR.")
            index += 2
            continue
        if article[index] == 0x0A:
            raise TestFailure("Incomplete or malformed article framing", "Article contains a bare LF.")
        index += 1


def _message_id_header(article: bytes) -> bytes | None:
    separator = article.find(b"\r\n\r\n")
    header_block = article[:separator]
    found: bytes | None = None
    lines = header_block.split(b"\r\n")
    index = 0
    while index < len(lines):
        line = lines[index]
        if line.startswith((b" ", b"\t")):
            return None
        name, sep, rest = line.partition(b":")
        if sep != b":":
            return None
        index += 1
        while index < len(lines) and lines[index].startswith((b" ", b"\t")):
            if name.lower() == b"message-id":
                return None
            index += 1
        if name.lower() != b"message-id":
            continue
        value = rest.strip(b" \t")
        if found is not None:
            return None
        found = value
    return found


def _amqp_shutdown_errors() -> tuple[type[BaseException], ...]:
    """Return the shutdown errors a best-effort close may ignore."""
    try:
        import pika.exceptions as pika_exceptions
    except ImportError:
        return (OSError,)
    return (OSError, pika_exceptions.AMQPError)


def _best_effort_close(connection: object) -> None:
    """Close a socket or AMQP connection without hiding unexpected failures.

    A closed socket raises ``OSError``. pika raises ``AMQPError`` when the
    connection is already gone. Either may happen while this process is
    finishing or reporting an earlier connection failure. Any other exception
    propagates.
    """
    closer = getattr(connection, "close", None)
    if closer is None:
        return
    with contextlib.suppress(*_amqp_shutdown_errors()):
        if getattr(connection, "is_open", True):
            closer()


class RabbitSession:
    """One AMQP connection and the exclusive reply queue for this test."""

    def __init__(self, connection: object, channel: object, reply_to: str) -> None:
        self.connection = connection
        self.channel = channel
        self.reply_to = reply_to

    def close(self) -> None:
        _best_effort_close(self.connection)


def connect_rabbit(config: Config) -> RabbitSession:
    pika = import_pika()
    connection = None
    try:
        params = _rabbit_parameters(pika, config)
        connection = pika.BlockingConnection(params)
        channel = connection.channel()
        channel.confirm_delivery()
        declared = channel.queue_declare(queue="", exclusive=True, auto_delete=True, durable=False)
        reply_to = declared.method.queue
        return RabbitSession(connection, channel, reply_to)
    except Exception as exc:
        if connection is not None:
            _best_effort_close(connection)
        raise TestFailure(
            "RabbitMQ RPC failure or timeout",
            "Could not open the RabbitMQ reply queue: " + redact(str(exc), config.secrets()),
        ) from None


def publish_and_wait(rabbit: RabbitSession, config: Config) -> tuple[RpcSuccess, float]:
    pika = import_pika()
    request_id = str(uuid.uuid4())
    correlation_id = str(uuid.uuid4())
    body = json.dumps(
        {
            "version": PROTOCOL_VERSION,
            "requestId": request_id,
            "messageId": config.message_id_text,
            "backbone": config.backbone,
        },
        separators=(",", ":"),
        ensure_ascii=True,
    ).encode("ascii")
    if len(body) > WORK_REQUEST_MAX_PAYLOAD_BYTES:
        raise TestFailure(
            "RabbitMQ RPC failure or timeout",
            "Article Work request exceeds WorkRequestMaxPayloadBytes (1024) and was not published.",
        )
    state: dict[str, object] = {"delivery": None, "ignored": 0, "returned": None}

    def on_return(channel: object, method: object, properties: object, unused_body: bytes) -> None:
        del channel, properties, unused_body
        text = getattr(method, "reply_text", None) or "unroutable"
        state["returned"] = str(text)

    def on_message(channel: object, method: object, properties: object, payload: bytes) -> None:
        del channel, method
        correlation = getattr(properties, "correlation_id", None)
        if correlation != correlation_id:
            state["ignored"] = int(state["ignored"]) + 1
            return
        state["delivery"] = (properties, bytes(payload))

    channel = rabbit.channel
    channel.add_on_return_callback(on_return)
    channel.basic_consume(queue=rabbit.reply_to, on_message_callback=on_message, auto_ack=True)
    properties = pika.BasicProperties(
        content_type=JSON_CONTENT_TYPE,
        correlation_id=correlation_id,
        reply_to=rabbit.reply_to,
        expiration=REQUEST_EXPIRATION_MILLISECONDS,
        delivery_mode=1,
        headers={"RequestId": request_id},
    )
    started = time.perf_counter()
    try:
        channel.basic_publish(
            exchange=config.exchange,
            routing_key=config.exchange,
            body=body,
            properties=properties,
            mandatory=True,
        )
    except Exception as exc:
        elapsed = time.perf_counter() - started
        raise TestFailure(
            "RabbitMQ RPC failure or timeout",
            f"Article Work publish to {config.exchange} failed: {redact(str(exc), config.secrets())}",
            rpc_elapsed=elapsed,
        ) from None
    print("[PASS] RabbitMQ RPC request published")
    deadline = time.monotonic() + config.rpc_timeout
    connection = rabbit.connection
    while state["delivery"] is None and state["returned"] is None:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        try:
            connection.process_data_events(time_limit=min(1.0, remaining))
        except Exception as exc:
            elapsed = time.perf_counter() - started
            raise TestFailure(
                "RabbitMQ RPC failure or timeout",
                "RabbitMQ connection failed while waiting for the RPC response: "
                + redact(str(exc), config.secrets()),
                rpc_elapsed=elapsed,
            ) from None
    elapsed = time.perf_counter() - started
    ignored = int(state["ignored"])
    if ignored:
        print(f"[NOTE] Ignored {ignored} RPC response(s) with a different correlation id.")
    if state["returned"] is not None and state["delivery"] is None:
        raise TestFailure(
            "RabbitMQ RPC failure or timeout",
            f"Article Work request was returned unroutable: {state['returned']}",
            rpc_elapsed=elapsed,
        )
    delivery = state["delivery"]
    if delivery is None:
        raise TestFailure(
            "RabbitMQ RPC failure or timeout",
            "No matching RPC response arrived before the timeout. "
            "ProviderFailure, Cancelled, UnexpectedFailure, and RetentionRejected are not published. "
            "The request expiration is 1000 ms, so a consumer that does not admit it in that window drops it.",
            rpc_elapsed=elapsed,
        )
    print("[PASS] Matching RPC response received")
    response_properties, payload = delivery
    try:
        success = parse_success_response(
            payload,
            response_properties,
            expected_request_id=uuid.UUID(request_id),
            expected_message_id=config.message_id_text,
            expected_backbone=config.backbone,
        )
    except TestFailure as exc:
        raise TestFailure(exc.category, exc.detail, rpc_elapsed=elapsed) from None
    return success, elapsed


def parse_success_response(
    payload: bytes,
    properties: object,
    *,
    expected_request_id: uuid.UUID,
    expected_message_id: str,
    expected_backbone: str,
) -> RpcSuccess:
    content_type = getattr(properties, "content_type", None)
    if content_type not in (None, "", JSON_CONTENT_TYPE):
        raise TestFailure(
            "RabbitMQ RPC failure or timeout",
            f"RPC response ContentType was {_preview(_as_bytes(content_type))}, expected application/json.",
        )
    expiration = getattr(properties, "expiration", None)
    if expiration not in (None, "", REQUEST_EXPIRATION_MILLISECONDS):
        raise TestFailure(
            "RabbitMQ RPC failure or timeout",
            "RPC response expiration was not 1000 milliseconds.",
        )
    header_request = _request_id_header(properties)
    if header_request is not None:
        try:
            header_id = uuid.UUID(header_request)
        except ValueError:
            raise TestFailure(
                "RabbitMQ RPC failure or timeout",
                "RPC response RequestId header is not a UUID.",
            ) from None
        if header_id != expected_request_id:
            raise TestFailure(
                "RabbitMQ RPC failure or timeout",
                "RPC response RequestId header does not match the request.",
            )
    try:
        document = json.loads(payload.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError):
        raise TestFailure("RabbitMQ RPC failure or timeout", "RPC response body was not UTF-8 JSON.") from None
    if not isinstance(document, dict):
        raise TestFailure("RabbitMQ RPC failure or timeout", "RPC response JSON root was not an object.")
    outcome = document.get("outcome")
    if outcome != "Success":
        error = document.get("error")
        error_text = error if isinstance(error, str) and error else "(no error field)"
        label = outcome if isinstance(outcome, str) else "(missing outcome)"
        raise TestFailure(
            "RabbitMQ RPC failure or timeout",
            f"RPC outcome was {label}: {error_text}",
        )
    _require_absent(document, "error")
    version = _require_int(document, "version")
    if version != PROTOCOL_VERSION:
        raise TestFailure("RabbitMQ RPC failure or timeout", f"RPC response version was {version}, expected 1.")
    request_text = _require_str(document, "requestId")
    try:
        request_id = uuid.UUID(request_text)
    except ValueError:
        raise TestFailure("RabbitMQ RPC failure or timeout", "RPC response requestId is not a UUID.") from None
    if request_id.int == 0 or request_id != expected_request_id:
        raise TestFailure("RabbitMQ RPC failure or timeout", "RPC response requestId does not match the request.")
    message_id = _require_str(document, "messageId")
    if message_id != expected_message_id:
        raise TestFailure("RabbitMQ RPC failure or timeout", "RPC response messageId does not match the request.")
    backbone = _require_str(document, "backbone")
    if backbone != expected_backbone:
        raise TestFailure("RabbitMQ RPC failure or timeout", "RPC response backbone does not match the request.")
    fqdn = _require_str(document, "fqdn")
    if not is_canonical_fqdn(fqdn):
        raise TestFailure("RabbitMQ RPC failure or timeout", "RPC response fqdn is not a lowercase dotted DNS name.")
    listener_port = _require_int(document, "listenerPort")
    if listener_port < 1 or listener_port > 65535:
        raise TestFailure("RabbitMQ RPC failure or timeout", "RPC response listenerPort is outside 1–65535.")
    article_id = _require_str(document, "articleId")
    if _ARTICLE_ID_HEX.fullmatch(article_id) is None:
        raise TestFailure(
            "RabbitMQ RPC failure or timeout",
            "RPC response articleId is not 64 lowercase hexadecimal characters.",
        )
    return RpcSuccess(request_id, message_id, backbone, fqdn, listener_port, article_id)


def import_pika() -> object:
    try:
        import pika
    except ImportError:
        raise ConfigError(
            "The pika package is required for AMQP. Install it with: python -m pip install pika"
        ) from None
    return pika


def _rabbit_parameters(pika: object, config: Config) -> object:
    if config.amqp_url:
        # URL host, port, vhost, credentials, and TLS are authoritative.
        # connection_attempts, retry_delay, and connection_name are this script's
        # connection behavior, not discrete broker settings.
        parameters = pika.URLParameters(config.amqp_url)
    else:
        credentials = pika.PlainCredentials(config.rabbit_user, config.rabbit_password)
        ssl_options = None
        if config.rabbit_tls:
            context = ssl.create_default_context(cafile=config.rabbit_ca_file)
            ssl_options = pika.SSLOptions(context, config.rabbit_host)
        parameters = pika.ConnectionParameters(
            host=config.rabbit_host,
            port=config.rabbit_port,
            virtual_host=config.rabbit_vhost,
            credentials=credentials,
            ssl_options=ssl_options,
            heartbeat=60,
            blocked_connection_timeout=config.connect_timeout,
            socket_timeout=config.connect_timeout,
            connection_attempts=1,
            retry_delay=0,
        )
    existing = getattr(parameters, "client_properties", None)
    merged: dict[str, object] = dict(existing) if isinstance(existing, dict) else {}
    merged["connection_name"] = CONNECTION_NAME
    parameters.client_properties = merged
    parameters.connection_attempts = 1
    parameters.retry_delay = 0
    return parameters


def validate_message_id(value: str) -> bytes:
    """Return RFC 5536 msg-id octets, including the angle brackets."""
    try:
        raw = value.encode("ascii")
    except UnicodeEncodeError:
        raise ConfigError("Message-ID must be ASCII.") from None
    if len(raw) < 3 or len(raw) > 250 or raw[0] != 0x3C or raw[-1] != 0x3E:
        raise ConfigError("Message-ID must be an RFC 5536 msg-id of 3–250 octets, including angle brackets.")
    interior = raw[1:-1]
    if interior.count(b"@") != 1:
        raise ConfigError("Message-ID must contain one @ between the angle brackets.")
    local, _, domain = interior.partition(b"@")
    if not _is_dot_atom(local) or not (_is_dot_atom(domain) or _is_no_fold_literal(domain)):
        raise ConfigError("Message-ID is not an RFC 5536 msg-id.")
    return raw


def is_canonical_fqdn(fqdn: str) -> bool:
    """Match ListenerEndpointFields: lowercase dotted DNS, labels 1–63, at least two labels."""
    if not fqdn or len(fqdn) > 253:
        return False
    labels = fqdn.split(".")
    if len(labels) < 2:
        return False
    for label in labels:
        if len(label) < 1 or len(label) > 63:
            return False
        if not (("a" <= label[0] <= "z") or ("0" <= label[0] <= "9")):
            return False
        if not (("a" <= label[-1] <= "z") or ("0" <= label[-1] <= "9")):
            return False
        for char in label:
            if not (("a" <= char <= "z") or ("0" <= char <= "9") or char == "-"):
                return False
    return True


def _is_dot_atom(token: bytes) -> bool:
    if not token or token.startswith(b".") or token.endswith(b".") or b".." in token:
        return False
    return all(part and all(byte in ATEXT for byte in part) for part in token.split(b"."))


def _is_no_fold_literal(token: bytes) -> bool:
    if len(token) < 2 or token[0] != 0x5B or token[-1] != 0x5D:
        return False
    for byte in token[1:-1]:
        if not (33 <= byte <= 61 or 63 <= byte <= 90 or 94 <= byte <= 126):
            return False
    return True


def _blake3_hex(message_id: bytes) -> str | None:
    try:
        import blake3
    except ImportError:
        return None
    return blake3.blake3(message_id).hexdigest()


def _request_id_header(properties: object) -> str | None:
    headers = getattr(properties, "headers", None) or {}
    if "RequestId" not in headers:
        return None
    value = headers["RequestId"]
    if isinstance(value, bytes):
        return value.decode("utf-8")
    if isinstance(value, str):
        return value
    return str(value)


def _require_str(document: dict[str, object], name: str) -> str:
    value = document.get(name)
    if not isinstance(value, str) or value == "":
        raise TestFailure("RabbitMQ RPC failure or timeout", f"RPC response field {name} is missing or not a string.")
    return value


def _require_int(document: dict[str, object], name: str) -> int:
    value = document.get(name)
    if isinstance(value, bool) or not isinstance(value, int):
        raise TestFailure("RabbitMQ RPC failure or timeout", f"RPC response field {name} is missing or not an integer.")
    return value


def _require_absent(document: dict[str, object], name: str) -> None:
    if name in document and document[name] is not None:
        raise TestFailure("RabbitMQ RPC failure or timeout", f"Success RPC response must not include {name}.")


def _send_quit(session: NntpSession) -> None:
    try:
        session._sock.settimeout(5)
        session.send(QUIT_COMMAND)
        session._max_line_content = MAX_STATUS_LINE_OCTETS - 2
        session.read_line()
    except (NntpIoError, OSError):
        return


def _idle_hint(config: Config, rpc_elapsed: float | None) -> str:
    del rpc_elapsed
    if config.rpc_timeout <= DEFAULT_NNTP_TIMEOUT_SECONDS:
        return ""
    return " The listener's per-read timeout may have closed the idle session before ARTICLE."


def _print_measurements(
    rpc_elapsed: float | None,
    article_elapsed: float | None,
    article_bytes: int | None,
) -> None:
    if rpc_elapsed is not None:
        print(f"RPC elapsed: {rpc_elapsed:.3f} s")
    if article_elapsed is not None:
        print(f"ARTICLE elapsed: {article_elapsed:.6f} s")
    if article_bytes is not None:
        print(f"Article bytes: {article_bytes}")


def _preview(data: bytes) -> str:
    return data[:180].decode("ascii", errors="backslashreplace")


def _as_bytes(value: object) -> bytes:
    if isinstance(value, bytes):
        return value
    return str(value).encode("ascii", errors="backslashreplace")


def redact(text: str, secrets: list[str]) -> str:
    redacted = _SECRET_URL.sub(r"\1", text)
    for secret in secrets:
        if secret:
            redacted = redacted.replace(secret, "***")
    return redacted


def _required_text(value: object, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ConfigError(f"{label} is required.")
    return value.strip()


def _optional_path(value: object) -> str | None:
    if not isinstance(value, str) or not value.strip():
        return None
    path = value.strip()
    if not os.path.isfile(path):
        raise ConfigError(f"CA file does not exist: {path}")
    return path


def _port(value: object, label: str) -> int:
    try:
        port = int(str(value))
    except (TypeError, ValueError):
        raise ConfigError(f"{label} must be an integer.") from None
    if port < 1 or port > 65535:
        raise ConfigError(f"{label} must be in the range 1–65535.")
    return port


def _positive_float(value: object, label: str) -> float:
    try:
        number = float(str(value))
    except (TypeError, ValueError):
        raise ConfigError(f"{label} must be a number of seconds.") from None
    if not math.isfinite(number):
        raise ConfigError(f"{label} must be a finite number of seconds.")
    if number <= 0:
        raise ConfigError(f"{label} must be greater than zero.")
    return number


def _positive_int(value: object, label: str) -> int:
    try:
        number = int(str(value))
    except (TypeError, ValueError):
        raise ConfigError(f"{label} must be an integer.") from None
    if number < 1:
        raise ConfigError(f"{label} must be at least 1.")
    return number


def _env_bool(name: str) -> bool:
    raw = os.environ.get(name)
    if raw is None or raw.strip() == "":
        return False
    text = raw.strip().lower()
    if text in {"1", "true", "yes", "on"}:
        return True
    if text in {"0", "false", "no", "off"}:
        return False
    raise ConfigError(f"{name} must be true or false.")


def _is_ip(host: str) -> bool:
    try:
        ipaddress.ip_address(host)
    except ValueError:
        return False
    return True


class _ChunkSocket:
    """In-memory socket for the local multiline reader check."""

    def __init__(self, chunks: list[bytes]) -> None:
        self._chunks = list(chunks)

    def recv(self, size: int) -> bytes:
        del size
        if not self._chunks:
            return b""
        return self._chunks.pop(0)

    def send(self, payload: bytes) -> int:
        return len(payload)

    def settimeout(self, timeout: float) -> None:
        del timeout

    def close(self) -> None:
        return None


def _best_effort_close_failures() -> list[str]:
    """Return failures from the local shutdown-suppression checks."""
    failures: list[str] = []

    class _ClosedSocket:
        def close(self) -> None:
            raise OSError("already closed")

    class _BrokenSocket:
        def close(self) -> None:
            raise RuntimeError("socket close bug")

    NntpSession(_ClosedSocket(), MAX_WIRE_LINE_CONTENT).close()
    try:
        NntpSession(_BrokenSocket(), MAX_WIRE_LINE_CONTENT).close()
    except RuntimeError as exc:
        if str(exc) != "socket close bug":
            failures.append("unexpected socket close error was replaced")
    else:
        failures.append("unexpected socket close error was ignored")

    class _ClosedAmqp:
        is_open = True

        def close(self) -> None:
            raise OSError("amqp socket closed")

    class _BrokenAmqp:
        is_open = True

        def close(self) -> None:
            raise RuntimeError("amqp close bug")

    RabbitSession(object(), None, "reply").close()

    class _IdleAmqp:
        is_open = False

        def close(self) -> None:
            raise RuntimeError("close called while idle")

    try:
        RabbitSession(_IdleAmqp(), None, "reply").close()
    except RuntimeError:
        failures.append("idle AMQP connection was closed")

    RabbitSession(_ClosedAmqp(), None, "reply").close()
    try:
        RabbitSession(_BrokenAmqp(), None, "reply").close()
    except RuntimeError as exc:
        if str(exc) != "amqp close bug":
            failures.append("unexpected AMQP close error was replaced")
    else:
        failures.append("unexpected AMQP close error was ignored")

    try:
        import pika.exceptions as pika_exceptions
    except ImportError:
        return failures

    class _PikaClosed:
        is_open = True

        def close(self) -> None:
            raise pika_exceptions.AMQPConnectionError("already gone")

    RabbitSession(_PikaClosed(), None, "reply").close()
    return failures


def _configuration_failures() -> list[str]:
    """Return configuration-precedence and timeout failures. Does not print secrets."""
    failures: list[str] = []
    managed = (
        "NNTP_HOST",
        "NNTP_PORT",
        "NNTP_TLS_SERVER_NAME",
        "NNTP_CA_FILE",
        "NNTP_INSECURE_SKIP_TLS_VERIFY",
        "BACKFILLER_MESSAGE_ID",
        "BACKFILLER_BACKBONE",
        "BACKFILLER_AMQP_URL",
        "RABBITMQ_HOST",
        "RABBITMQ_PORT",
        "RABBITMQ_VHOST",
        "RABBITMQ_CA_FILE",
        "RABBITMQ_USE_TLS",
        "VECTOR__RABBITMQ__USERNAME",
        "VECTOR__RABBITMQ__PASSWORD",
        "BACKFILLER_CONNECT_TIMEOUT_SECONDS",
        "BACKFILLER_RPC_TIMEOUT_SECONDS",
        "BACKFILLER_NNTP_TIMEOUT_SECONDS",
        "BACKFILLER_MAX_ARTICLE_BYTES",
    )
    base = [
        "--nntp-host",
        "nntp.example",
        "--nntp-port",
        "563",
        "--backbone",
        "Giganews",
        "--message-id",
        "<a@b.example>",
    ]
    discrete = {
        "RABBITMQ_HOST": "broker.example",
        "VECTOR__RABBITMQ__USERNAME": "discrete-user",
        "VECTOR__RABBITMQ__PASSWORD": "discrete-secret",
    }
    amqp_url = "amqp://url-user:url-secret@url-broker.example:5672/vh"
    saved = {key: os.environ.get(key) for key in managed}

    def build(argv: list[str], env: dict[str, str]) -> Config:
        for key in managed:
            os.environ.pop(key, None)
        os.environ.update(env)
        return build_config(parse_args(base + argv))

    def note(condition: bool, detail: str) -> None:
        if not condition:
            failures.append(detail)

    def expect_error(argv: list[str], env: dict[str, str], fragment: str, label: str) -> None:
        try:
            build(argv, env)
        except ConfigError as exc:
            note(fragment in str(exc), f"{label}: message did not include {fragment!r}")
        else:
            failures.append(f"{label}: configuration was accepted")

    try:
        disabled = build(["--no-insecure-skip-tls-verify"], {**discrete, "NNTP_INSECURE_SKIP_TLS_VERIFY": "true"})
        note(disabled.insecure_tls is False, "CLI did not disable NNTP verification when the environment enabled it")
        enabled = build(["--insecure-skip-tls-verify"], {**discrete, "NNTP_INSECURE_SKIP_TLS_VERIFY": "false"})
        note(enabled.insecure_tls is True, "CLI did not enable NNTP verification skipping when the environment disabled it")
        inherited = build([], {**discrete, "NNTP_INSECURE_SKIP_TLS_VERIFY": "true"})
        note(inherited.insecure_tls is True, "omitted NNTP verification flag did not inherit the environment")
        defaulted = build([], discrete)
        note(defaulted.insecure_tls is False, "NNTP verification default was not certificate checking")

        rabbit_off = build(["--no-rabbitmq-tls"], {**discrete, "RABBITMQ_USE_TLS": "true"})
        note(rabbit_off.rabbit_tls is False, "CLI did not disable RabbitMQ TLS when the environment enabled it")
        rabbit_on = build(["--rabbitmq-tls"], {**discrete, "RABBITMQ_USE_TLS": "false"})
        note(rabbit_on.rabbit_tls is True, "CLI did not enable RabbitMQ TLS when the environment disabled it")
        rabbit_env = build([], {**discrete, "RABBITMQ_USE_TLS": "true"})
        note(rabbit_env.rabbit_tls is True, "omitted RabbitMQ TLS flag did not inherit the environment")
        note(defaulted.rabbit_tls is False, "RabbitMQ TLS default was not plaintext")

        url_config = build(
            ["--amqp-url", amqp_url],
            {
                "RABBITMQ_HOST": "discrete.example",
                "RABBITMQ_PORT": "not-a-port",
                "RABBITMQ_VHOST": "other",
                "RABBITMQ_CA_FILE": "missing-ca-not-real.pem",
                "RABBITMQ_USE_TLS": "true",
                "VECTOR__RABBITMQ__USERNAME": "discrete-user",
                "VECTOR__RABBITMQ__PASSWORD": "discrete-secret",
            },
        )
        note(url_config.amqp_url == amqp_url, "AMQP URL was not kept as the broker configuration")
        note(url_config.rabbit_host is None, "URL mode kept a discrete host")
        note(url_config.rabbit_user is None, "URL mode kept a discrete username")
        note(url_config.rabbit_password is None, "URL mode kept a discrete password")
        note(url_config.rabbit_tls is False, "URL mode kept discrete TLS")
        note(url_config.rabbit_ca_file is None, "URL mode kept a discrete CA file")
        note(url_config.rabbit_vhost == DEFAULT_RABBITMQ_VHOST, "URL mode kept a discrete virtual host")

        bare_url = build(
            ["--amqp-url", amqp_url, "--rabbitmq-port=not-a-port", "--rabbitmq-ca-file=missing-ca-not-real.pem"],
            {},
        )
        note(bare_url.rabbit_user is None and bare_url.rabbit_password is None, "URL mode required discrete credentials")
        note(bare_url.rabbit_ca_file is None, "URL mode validated an explicit unused CA file")
        amqps_url = "amqps://url-user:url-secret@url-broker.example:5671/vh"
        amqps_config = build(
            ["--amqp-url", amqps_url],
            {"RABBITMQ_USE_TLS": "false", "RABBITMQ_CA_FILE": "missing-ca-not-real.pem"},
        )
        note(amqps_config.amqp_url == amqps_url, "amqps URL was not kept")
        note(amqps_config.rabbit_ca_file is None, "amqps URL mode validated a discrete CA file")

        host_override = build(
            [
                "--rabbitmq-host",
                "cli-broker.example",
                "--rabbitmq-port",
                "5671",
                "--rabbitmq-username",
                "cli-user",
                "--rabbitmq-password",
                "cli-secret",
            ],
            {"BACKFILLER_AMQP_URL": amqp_url, "RABBITMQ_PORT": "not-a-port"},
        )
        note(host_override.amqp_url is None, "explicit --rabbitmq-host did not override the environment AMQP URL")
        note(host_override.rabbit_host == "cli-broker.example", "explicit host was not used")
        note(host_override.rabbit_port == 5671, "explicit port did not override the environment port")
        note(host_override.rabbit_user == "cli-user", "explicit username was not used")

        valid_discrete = build([], discrete)
        note(valid_discrete.amqp_url is None, "discrete mode stored an AMQP URL")
        note(valid_discrete.rabbit_host == "broker.example", "discrete host was not used")
        note(valid_discrete.rabbit_port == DEFAULT_RABBITMQ_PORT, "discrete port default was not 5672")
        note(valid_discrete.rabbit_user == "discrete-user", "discrete username was not used")
        note(valid_discrete.connect_timeout == DEFAULT_CONNECT_TIMEOUT_SECONDS, "connect timeout default changed")
        note(valid_discrete.rpc_timeout == DEFAULT_RPC_TIMEOUT_SECONDS, "RPC timeout default changed")
        note(valid_discrete.nntp_timeout == DEFAULT_NNTP_TIMEOUT_SECONDS, "NNTP timeout default changed")
        note(
            math.isfinite(valid_discrete.connect_timeout)
            and math.isfinite(valid_discrete.rpc_timeout)
            and math.isfinite(valid_discrete.nntp_timeout),
            "a non-finite timeout reached configuration",
        )

        expect_error([], {**discrete, "RABBITMQ_PORT": "not-a-port"}, "RabbitMQ port", "invalid discrete port")
        expect_error(
            [],
            {**discrete, "RABBITMQ_CA_FILE": "missing-ca-not-real.pem"},
            "CA file does not exist",
            "missing discrete CA file",
        )
        expect_error(
            ["--amqp-url", amqp_url, "--rabbitmq-host", "broker.example"],
            {},
            "not both",
            "URL and host flags together",
        )

        note(_positive_float("1.5", "RPC timeout") == 1.5, "a positive finite timeout was rejected")

        def expect_float(value: str, fragment: str, label: str) -> None:
            try:
                _positive_float(value, "RPC timeout")
            except ConfigError as exc:
                note(fragment in str(exc), f"{label}: message did not include {fragment!r}")
            else:
                failures.append(f"{label}: timeout was accepted")

        expect_float("0", "greater than zero", "zero timeout")
        expect_float("-1", "greater than zero", "negative timeout")
        for value in ("nan", "NaN", "inf", "+inf", "-inf", "Infinity", "-Infinity"):
            expect_float(value, "finite", f"non-finite timeout {value}")
            expect_error([f"--rpc-timeout={value}"], discrete, "finite", f"non-finite RPC timeout {value}")
            expect_error([f"--connect-timeout={value}"], discrete, "finite", f"non-finite connect timeout {value}")
            expect_error([f"--nntp-timeout={value}"], discrete, "finite", f"non-finite NNTP timeout {value}")
        expect_float("abc", "number of seconds", "malformed timeout")
        expect_error(["--rpc-timeout=0"], discrete, "greater than zero", "zero RPC timeout")
        expect_error(["--rpc-timeout=-1"], discrete, "greater than zero", "negative RPC timeout")
        expect_error(["--rpc-timeout=abc"], discrete, "number of seconds", "malformed RPC timeout")
        expect_error(
            [],
            {**discrete, "BACKFILLER_RPC_TIMEOUT_SECONDS": "nan"},
            "finite",
            "non-finite environment RPC timeout",
        )

        try:
            pika = import_pika()
        except ConfigError:
            pika = None
        if pika is not None:
            parameters = _rabbit_parameters(pika, url_config)
            note(parameters.host == "url-broker.example", "URL parameters did not use the URL host")
            note(parameters.port == 5672, "URL parameters did not use the URL port")
            note(parameters.virtual_host == "vh", "URL parameters did not use the URL virtual host")
            credentials = parameters.credentials
            note(
                getattr(credentials, "username", None) == "url-user",
                "URL parameters did not use the URL username",
            )
            note(getattr(parameters, "ssl_options", None) is None, "URL mode applied discrete TLS or CA settings")
            note(parameters.connection_attempts == 1, "URL mode dropped connection_attempts")
            note(parameters.retry_delay == 0, "URL mode dropped retry_delay")
            secure_parameters = _rabbit_parameters(pika, amqps_config)
            note(getattr(secure_parameters, "ssl_options", None) is not None, "amqps URL did not select TLS")
            note(secure_parameters.port == 5671, "amqps URL port was replaced")
            discrete_parameters = _rabbit_parameters(pika, valid_discrete)
            note(discrete_parameters.host == "broker.example", "discrete parameters did not use the discrete host")
            note(getattr(discrete_parameters, "ssl_options", None) is None, "plaintext discrete mode enabled TLS")
    finally:
        for key, previous in saved.items():
            if previous is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = previous
    return failures


def run_self_test() -> int:
    failures: list[str] = []

    def check(condition: bool, detail: str) -> None:
        if not condition:
            failures.append(detail)

    session = NntpSession(
        _ChunkSocket([b"Hello\r", b"\n..dot\r\n.\r\n"]),
        MAX_WIRE_LINE_CONTENT,
    )
    article = session.read_multiline(1024)
    check(article == b"Hello\r\n.dot\r\n", "destuff of a split CRLF and a stuffed dot line failed")

    stuffed_dot = NntpSession(_ChunkSocket([b"..\r\n.\r\n"]), MAX_WIRE_LINE_CONTENT)
    check(stuffed_dot.read_multiline(1024) == b".\r\n", "a stuffed single-dot line was treated as the terminator")

    try:
        NntpSession(_ChunkSocket([b"partial"]), MAX_WIRE_LINE_CONTENT).read_line()
        check(False, "a closed socket with an unfinished line did not fail")
    except NntpIoError as exc:
        check(exc.kind == "disconnect", "unfinished line was not classified as a disconnect")

    check(validate_message_id("<12345@example.invalid>") == b"<12345@example.invalid>", "canonical message-id was rejected")
    check(validate_message_id("<a@[127.0.0.1]>") == b"<a@[127.0.0.1]>", "no-fold-literal message-id was rejected")
    for bad in ("12345@example.invalid", "<a@>", "<a..b@c>", "<.a@b>", "<>"):
        try:
            validate_message_id(bad)
        except ConfigError:
            rejected = True
        else:
            rejected = False
        check(rejected, f"invalid message-id was accepted: {bad}")
    failures.extend(_best_effort_close_failures())
    failures.extend(_configuration_failures())

    check(is_canonical_fqdn("backfiller01.usenet.ninja"), "canonical fqdn was rejected")
    check(not is_canonical_fqdn("Backfiller01.usenet.ninja"), "uppercase fqdn was accepted")
    check(not is_canonical_fqdn("localhost"), "single-label name was accepted")

    class _Props:
        content_type = JSON_CONTENT_TYPE
        expiration = REQUEST_EXPIRATION_MILLISECONDS
        headers = {"RequestId": "7c1cb8a0-95f9-4c13-8e53-339773e3afaa"}

    body = (
        b'{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa",'
        b'"messageId":"<12345@example.invalid>","backbone":"Giganews","outcome":"Success",'
        b'"fqdn":"backfiller01.usenet.ninja","listenerPort":119,'
        b'"articleId":"dcab316ba0e91c6abbad8d5759bff207932dbe9168c88954c6dd9240b4a6da14"}'
    )
    parsed = parse_success_response(
        body,
        _Props(),
        expected_request_id=uuid.UUID("7c1cb8a0-95f9-4c13-8e53-339773e3afaa"),
        expected_message_id="<12345@example.invalid>",
        expected_backbone="Giganews",
    )
    check(parsed.listener_port == 119, "success listenerPort was not read")
    digest = _blake3_hex(b"<12345@example.invalid>")
    if digest is not None:
        check(digest == parsed.article_id, "BLAKE3 of the fixture Message-ID did not match articleId")

    not_found = (
        b'{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa",'
        b'"messageId":"<12345@example.invalid>","backbone":"Giganews",'
        b'"outcome":"ArticleNotFound","error":"No article with that message-id"}'
    )
    try:
        parse_success_response(
            not_found,
            _Props(),
            expected_request_id=uuid.UUID("7c1cb8a0-95f9-4c13-8e53-339773e3afaa"),
            expected_message_id="<12345@example.invalid>",
            expected_backbone="Giganews",
        )
        check(False, "ArticleNotFound was treated as success")
    except TestFailure as exc:
        check(exc.category == "RabbitMQ RPC failure or timeout", "ArticleNotFound used the wrong failure class")

    header_article = b"Message-ID: <12345@example.invalid>\r\n\r\nbody\r\n"
    check(_message_id_header(header_article) == b"<12345@example.invalid>", "Message-ID header was not extracted")
    check(redact("amqps://user:secret@broker/vhost", ["secret"]) == "amqps://broker/vhost", "URL credentials were not redacted")

    if failures:
        for detail in failures:
            print(f"[FAIL] {detail}")
        print()
        print("Result: FAIL")
        print("Failure: local self-test")
        return 1
    print("[PASS] Multiline dot-stuffing")
    print("[PASS] Message-ID grammar")
    print("[PASS] RPC success and failure schema")
    print("[PASS] Configuration precedence and finite timeouts")
    print()
    print("Result: PASS")
    print("Live services were not contacted.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

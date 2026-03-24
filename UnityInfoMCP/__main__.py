from __future__ import annotations

import argparse
import sys
import traceback

from .server import mcp

DEFAULT_MCP_PORT = 16000
DEFAULT_MCP_TRANSPORT = "streamable-http"
SUPPORTED_MCP_TRANSPORTS = ("stdio", "streamable-http")


def _parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run UnityInfoMCP.")
    parser.add_argument(
        "--transport",
        choices=SUPPORTED_MCP_TRANSPORTS,
        default=DEFAULT_MCP_TRANSPORT,
        help="MCP transport to use. Defaults to streamable-http.",
    )
    parser.add_argument(
        "--port",
        type=int,
        help=(
            "Run Streamable HTTP transport on the specified port instead of "
            f"the default {DEFAULT_MCP_PORT}."
        ),
    )
    args = parser.parse_args(argv)
    if args.transport != "streamable-http" and args.port is not None:
        parser.error("--port can only be used with --transport streamable-http.")
    if args.port is not None and not (1 <= args.port <= 65535):
        parser.error("--port must be between 1 and 65535.")
    return args


def _wait_for_exit(exc: BaseException, *, pause: bool) -> None:
    print("UnityInfoMCP failed to start.", file=sys.stderr)
    traceback.print_exception(type(exc), exc, exc.__traceback__)
    if pause:
        try:
            input("Press Enter to exit...")
        except EOFError:
            pass


def main(argv: list[str] | None = None) -> None:
    args = _parse_args(argv)
    transport = args.transport
    if transport == "streamable-http":
        mcp.settings.port = args.port if args.port is not None else DEFAULT_MCP_PORT

    try:
        mcp.run(transport=transport)
    except KeyboardInterrupt:
        return
    except BaseException as exc:
        _wait_for_exit(exc, pause=transport != "stdio" and sys.stdin.isatty())
        raise SystemExit(1) from exc


if __name__ == "__main__":
    main()

class BridgeError(Exception):
    """Base class for bridge-related exceptions."""


class BridgeUnavailableError(BridgeError):
    """Raised when the external bridge cannot be reached."""


class BridgeProtocolError(BridgeError):
    """Raised when the bridge returns malformed payloads."""


class BridgeRemoteError(BridgeError):
    """Raised when the bridge returns a JSON-RPC error."""

    def __init__(self, code: int, message: str, data: object | None = None) -> None:
        super().__init__(f"[{code}] {message}")
        self.code = code
        self.message = message
        self.data = data

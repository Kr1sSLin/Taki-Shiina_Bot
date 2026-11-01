"""Handler-level logging boundary which never formats application payloads."""
from __future__ import annotations

import logging
import re

_TOKEN = re.compile(r"[A-Za-z_][A-Za-z0-9_.-]{0,79}\Z")


def _token(value, fallback: str) -> str:
    return value if isinstance(value, str) and _TOKEN.fullmatch(value) else fallback


class _SanitisedLogRecord(logging.LogRecord):
    __slots__ = ("_privacy_exception_type",)


class PrivacyLogFilter(logging.Filter):
    """Keep event category, severity, timing and exception class only.

    Attach to every output handler, including handlers owned by third-party
    libraries. Application messages, interpolation arguments, exception text,
    traceback/stack data and arbitrary extra fields are never formatted. An
    explicit ``privacy_event`` is accepted only from the constructor's static
    allowlist; otherwise the logger's category is used. No model call is made.
    Uses replacement records supported by the locked Python 3.14 environment
    (Python 3.12+), so multiple handlers retain the same exception class.
    """

    def __init__(self, *, allowed_events=()):
        super().__init__()
        self.allowed_events = frozenset(_token(event, "application") for event in allowed_events)

    def filter(self, record: logging.LogRecord) -> logging.LogRecord:
        category = _token(record.name, "application")
        requested = getattr(record, "privacy_event", None)
        event = requested if isinstance(requested, str) and requested in self.allowed_events else category
        exception_type = "none"
        if isinstance(record, _SanitisedLogRecord):
            exception_type = record._privacy_exception_type
        elif isinstance(record.exc_info, tuple) and record.exc_info and isinstance(record.exc_info[0], type):
            exception_type = _token(record.exc_info[0].__name__, "Exception")
        elif isinstance(record.args, tuple):
            exceptions = [arg for arg in record.args if isinstance(arg, BaseException)]
            if exceptions:
                exception_type = _token(type(exceptions[0]).__name__, "Exception")
        elif isinstance(record.msg, BaseException):
            exception_type = _token(type(record.msg).__name__, "Exception")

        safe = _SanitisedLogRecord(
            name=category, level=record.levelno, pathname="", lineno=record.lineno,
            msg=f"event={event} exception={exception_type}", args=(), exc_info=None,
            func=_token(record.funcName, "unknown"), sinfo=None,
        )
        safe._privacy_exception_type = exception_type
        for field in ("created", "msecs", "relativeCreated"):
            value = getattr(record, field, None)
            if isinstance(value, (int, float)):
                setattr(safe, field, value)
        for field in ("threadName", "processName", "taskName"):
            if hasattr(safe, field):
                setattr(safe, field, _token(getattr(safe, field), "worker"))
        return safe


def protect_log_handler(handler: logging.Handler, *, allowed_events=()) -> logging.Handler:
    """Idempotently attach the privacy boundary to an existing output handler."""
    formatter = handler.formatter
    if formatter is not None and any(
        base.__module__ == "uvicorn.logging" and base.__name__ == "AccessFormatter"
        for base in type(formatter).__mro__
    ):
        # Uvicorn's access formatter unpacks five original HTTP arguments.
        # Those arguments contain the URI and cannot cross this boundary.
        handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s %(message)s"))
    if not any(isinstance(item, PrivacyLogFilter) for item in handler.filters):
        handler.addFilter(PrivacyLogFilter(allowed_events=allowed_events))
    return handler


def install_privacy_logging(*, allowed_events=()) -> int:
    """Protect every currently registered root or third-party output handler.

    A safe record factory also prevents standard message/exception fields from
    leaking through handlers added later. Custom structured handlers still need
    protect_log_handler: logging adds arbitrary ``extra`` fields after invoking
    a record factory. Call again after adding any such output handlers.
    Filtering the root logger alone would not protect propagated records.
    """
    loggers = [logging.getLogger()]
    loggers.extend(item for item in list(logging.Logger.manager.loggerDict.values()) if isinstance(item, logging.Logger))
    handlers = {handler for logger in loggers for handler in logger.handlers}
    for handler in handlers:
        protect_log_handler(handler, allowed_events=allowed_events)
    previous_factory = logging.getLogRecordFactory()
    if not getattr(previous_factory, "_privacy_boundary_factory", False):
        boundary = PrivacyLogFilter()

        def privacy_record_factory(*args, **kwargs):
            return boundary.filter(previous_factory(*args, **kwargs))

        privacy_record_factory._privacy_boundary_factory = True
        logging.setLogRecordFactory(privacy_record_factory)
    return len(handlers)

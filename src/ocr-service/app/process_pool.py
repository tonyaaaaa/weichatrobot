import asyncio
import multiprocessing
import time
from multiprocessing.connection import Connection
from typing import Any

from .ocr_engine import OcrEngine


class OcrPageTimeoutError(TimeoutError):
    pass


class OcrWorkerError(RuntimeError):
    pass


def _worker_main(connection: Connection, engine: OcrEngine) -> None:
    try:
        initialize = getattr(engine, "initialize", None)
        if initialize is not None:
            initialize()
        connection.send(("ready", None))
        while True:
            command, payload = connection.recv()
            if command == "stop":
                return
            try:
                connection.send(("result", engine.recognize(payload)))
            except Exception:
                connection.send(("error", None))
    except (EOFError, BrokenPipeError):
        return
    except Exception:
        try:
            connection.send(("init_error", None))
        except (EOFError, BrokenPipeError, OSError):
            pass
    finally:
        connection.close()


class OcrProcessPool:
    """Owns exactly one OCR worker and serializes access to its model instance."""

    def __init__(self, engine: OcrEngine, *, startup_timeout_seconds: float = 30.0):
        self._engine = engine
        self._startup_timeout_seconds = startup_timeout_seconds
        self._context = multiprocessing.get_context("spawn")
        self._process: multiprocessing.Process | None = None
        self._connection: Connection | None = None
        self._lock = asyncio.Lock()
        self.worker_pids: list[int] = []

    async def ready(self) -> bool:
        async with self._lock:
            try:
                await self._ensure_worker()
                return True
            except OcrWorkerError:
                return False

    async def recognize(self, image_bytes: bytes, *, timeout_seconds: float) -> list[tuple[str, float]]:
        async with self._lock:
            await self._ensure_worker()
            assert self._connection is not None
            try:
                self._connection.send(("recognize", image_bytes))
            except (BrokenPipeError, EOFError, OSError) as exception:
                self._stop_worker()
                raise OcrWorkerError("OCR worker exited.") from exception

            message = await self._receive(timeout_seconds)
            if message is None:
                self._stop_worker()
                raise OcrPageTimeoutError("OCR worker timed out.")
            kind, payload = message
            if kind == "result":
                return payload
            if kind == "error":
                raise OcrWorkerError("OCR engine failed.")
            self._stop_worker()
            raise OcrWorkerError("OCR worker exited.")

    async def _ensure_worker(self) -> None:
        if self._process is not None and self._process.is_alive() and self._connection is not None:
            return
        self._stop_worker()
        parent, child = self._context.Pipe()
        process = self._context.Process(target=_worker_main, args=(child, self._engine), daemon=True)
        process.start()
        child.close()
        self._process = process
        self._connection = parent
        if process.pid is not None:
            self.worker_pids.append(process.pid)
        message = await self._receive(self._startup_timeout_seconds)
        if message is None or message[0] != "ready":
            self._stop_worker()
            raise OcrWorkerError("OCR worker failed to initialize.")

    async def _receive(self, timeout_seconds: float) -> tuple[str, Any] | None:
        deadline = time.monotonic() + timeout_seconds
        while time.monotonic() < deadline:
            if self._connection is None or self._process is None:
                return None
            try:
                has_message = self._connection.poll()
            except (BrokenPipeError, EOFError, OSError):
                return ("worker_exit", None)
            if has_message:
                try:
                    return self._connection.recv()
                except (EOFError, OSError):
                    return ("worker_exit", None)
            if not self._process.is_alive():
                return ("worker_exit", None)
            await asyncio.sleep(min(0.005, max(0, deadline - time.monotonic())))
        return None

    def close(self) -> None:
        self._stop_worker(graceful=True)

    def _stop_worker(self, *, graceful: bool = False) -> None:
        process, connection = self._process, self._connection
        self._process = None
        self._connection = None
        if graceful and connection is not None and process is not None and process.is_alive():
            try:
                connection.send(("stop", None))
                process.join(timeout=0.25)
            except (BrokenPipeError, EOFError, OSError):
                pass
        if process is not None and process.is_alive():
            process.terminate()
        if process is not None:
            process.join(timeout=2)
            if process.is_alive():
                process.kill()
                process.join(timeout=2)
            process.close()
        if connection is not None:
            connection.close()

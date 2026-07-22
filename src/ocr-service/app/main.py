import base64
import logging
import os
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from pydantic import ValidationError

from .models import OcrPageRequest, OcrPagesRequest
from .ocr_engine import ImageValidationError, OcrEngine, create_engine, validate_image
from .process_pool import OcrPageTimeoutError, OcrProcessPool

logger = logging.getLogger(__name__)


def create_app(
    engine: OcrEngine,
    *,
    maximum_request_bytes: int = 25 * 1024 * 1024,
    maximum_image_pixels: int = 16_000_000,
    maximum_image_width: int = 10_000,
    maximum_image_height: int = 10_000,
    page_timeout_seconds: float = 15,
) -> FastAPI:
    pool = OcrProcessPool(engine)

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        yield
        pool.close()

    app = FastAPI(title="WechatRobot OCR", docs_url=None, redoc_url=None, lifespan=lifespan)
    app.state.ocr_pool = pool

    @app.middleware("http")
    async def bound_request_body(request: Request, call_next):
        declared = request.headers.get("content-length")
        if declared and declared.isdigit() and int(declared) > maximum_request_bytes:
            return JSONResponse(status_code=413, content={"detail": "Request body is too large."})
        body = bytearray()
        async for chunk in request.stream():
            if len(body) + len(chunk) > maximum_request_bytes:
                return JSONResponse(status_code=413, content={"detail": "Request body is too large."})
            body.extend(chunk)
        request._body = bytes(body)

        delivered = False

        async def receive():
            nonlocal delivered
            if delivered:
                return {"type": "http.request", "body": b"", "more_body": False}
            delivered = True
            return {"type": "http.request", "body": request._body, "more_body": False}

        request._receive = receive
        return await call_next(request)

    @app.get("/health")
    @app.get("/health/live")
    async def health():
        return {"status": "ok"}

    @app.get("/health/ready")
    async def ready():
        if await pool.ready():
            return {"status": "ready"}
        return JSONResponse(status_code=503, content={"status": "not_ready"})

    @app.post("/v1/ocr/pages")
    async def recognize_pages(request: Request):
        try:
            payload = OcrPagesRequest.model_validate_json(await request.body())
        except ValidationError as exception:
            return JSONResponse(
                status_code=422,
                content={"detail": exception.errors(include_context=False, include_input=False)},
            )

        decoded_pages: list[tuple[OcrPageRequest, bytes]] = []
        for page in payload.pages:
            image_bytes = base64.b64decode(page.image_base64, validate=True)
            try:
                validate_image(
                    image_bytes,
                    declared_width=page.width,
                    declared_height=page.height,
                    maximum_width=maximum_image_width,
                    maximum_height=maximum_image_height,
                    maximum_pixels=maximum_image_pixels,
                )
            except ImageValidationError as exception:
                return JSONResponse(status_code=422, content={"detail": str(exception)})
            decoded_pages.append((page, image_bytes))

        async def recognize(page: OcrPageRequest, image_bytes: bytes):
            try:
                blocks = await pool.recognize(image_bytes, timeout_seconds=page_timeout_seconds)
                return {
                    "pageNumber": page.page_number,
                    "status": "completed",
                    "blocks": [
                        {"order": order, "text": text, "confidence": confidence}
                        for order, (text, confidence) in enumerate(blocks)
                    ],
                    "error": None,
                }
            except OcrPageTimeoutError:
                return {
                    "pageNumber": page.page_number,
                    "status": "timeout",
                    "blocks": [],
                    "error": "OCR page timed out.",
                }
            except Exception:
                logger.exception("OCR page failed in isolated worker")
                return {
                    "pageNumber": page.page_number,
                    "status": "failed",
                    "blocks": [],
                    "error": "OCR page failed.",
                }

        pages = []
        for page, image in decoded_pages:
            pages.append(await recognize(page, image))
        return {"pages": pages}

    return app


def create_configured_app() -> FastAPI:
    return create_app(
        create_engine(os.getenv("OCR_ENGINE", "paddle")),
        maximum_request_bytes=int(os.getenv("OCR_MAX_REQUEST_BYTES", str(25 * 1024 * 1024))),
        maximum_image_pixels=int(os.getenv("OCR_MAX_IMAGE_PIXELS", "16000000")),
        maximum_image_width=int(os.getenv("OCR_MAX_IMAGE_WIDTH", "10000")),
        maximum_image_height=int(os.getenv("OCR_MAX_IMAGE_HEIGHT", "10000")),
        page_timeout_seconds=float(os.getenv("OCR_PAGE_TIMEOUT_SECONDS", "15")),
    )


app = create_configured_app()

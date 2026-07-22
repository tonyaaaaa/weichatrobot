import asyncio
import base64
import binascii
import os
from typing import Protocol

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, ConfigDict, Field, ValidationError, field_validator


class OcrEngine(Protocol):
    def recognize(self, image_bytes: bytes) -> list[tuple[str, float]]: ...


class OcrPageRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True)
    page_number: int = Field(alias="pageNumber", ge=1)
    image_base64: str = Field(alias="imageBase64", min_length=1)
    width: int = Field(ge=1)
    height: int = Field(ge=1)

    @field_validator("image_base64")
    @classmethod
    def validate_base64(cls, value: str) -> str:
        try:
            base64.b64decode(value, validate=True)
        except (binascii.Error, ValueError) as exception:
            raise ValueError("imageBase64 must be valid base64") from exception
        return value


class OcrPagesRequest(BaseModel):
    pages: list[OcrPageRequest] = Field(min_length=1, max_length=500)


def create_app(
    engine: OcrEngine,
    *,
    maximum_request_bytes: int = 25 * 1024 * 1024,
    maximum_image_pixels: int = 16_000_000,
    page_timeout_seconds: float = 15,
) -> FastAPI:
    app = FastAPI(title="WechatRobot OCR", docs_url=None, redoc_url=None)

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
    async def health():
        return {"status": "ok"}

    @app.post("/v1/ocr/pages")
    async def recognize_pages(request: Request):
        try:
            payload = OcrPagesRequest.model_validate_json(await request.body())
        except ValidationError as exception:
            return JSONResponse(status_code=422, content={"detail": exception.errors(include_context=False, include_input=False)})
        for page in payload.pages:
            if page.width * page.height > maximum_image_pixels:
                return JSONResponse(status_code=422, content={"detail": "Image pixel limit exceeded."})

        async def recognize(page: OcrPageRequest):
            try:
                image = base64.b64decode(page.image_base64, validate=True)
                blocks = await asyncio.wait_for(asyncio.to_thread(engine.recognize, image), timeout=page_timeout_seconds)
                return {
                    "pageNumber": page.page_number,
                    "status": "completed",
                    "blocks": [
                        {"order": order, "text": text, "confidence": confidence}
                        for order, (text, confidence) in enumerate(blocks)
                    ],
                    "error": None,
                }
            except TimeoutError:
                return {"pageNumber": page.page_number, "status": "timeout", "blocks": [], "error": "OCR page timed out."}
            except Exception:
                return {"pageNumber": page.page_number, "status": "failed", "blocks": [], "error": "OCR page failed."}

        pages = await asyncio.gather(*(recognize(page) for page in payload.pages))
        return {"pages": list(pages)}

    return app


class PaddleOcrEngine:
    def __init__(self):
        self._engine = None

    def recognize(self, image_bytes: bytes) -> list[tuple[str, float]]:
        if self._engine is None:
            from paddleocr import PaddleOCR
            self._engine = PaddleOCR(use_doc_orientation_classify=False, use_doc_unwarping=False, use_textline_orientation=False)
        import numpy
        from PIL import Image
        from io import BytesIO
        image = numpy.asarray(Image.open(BytesIO(image_bytes)).convert("RGB"))
        results = self._engine.predict(image)
        blocks: list[tuple[str, float]] = []
        for result in results:
            data = result.json.get("res", {})
            for text, confidence in zip(data.get("rec_texts", []), data.get("rec_scores", [])):
                blocks.append((str(text), float(confidence)))
        return blocks


app = create_app(
    PaddleOcrEngine(),
    maximum_request_bytes=int(os.getenv("OCR_MAX_REQUEST_BYTES", str(25 * 1024 * 1024))),
    maximum_image_pixels=int(os.getenv("OCR_MAX_IMAGE_PIXELS", "16000000")),
    page_timeout_seconds=float(os.getenv("OCR_PAGE_TIMEOUT_SECONDS", "15")),
)

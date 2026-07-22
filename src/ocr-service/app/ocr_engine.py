import io
import warnings
from typing import Protocol

from PIL import Image, UnidentifiedImageError


class OcrEngine(Protocol):
    def recognize(self, image_bytes: bytes) -> list[tuple[str, float]]: ...


class ImageValidationError(ValueError):
    pass


def validate_image(
    image_bytes: bytes,
    *,
    declared_width: int,
    declared_height: int,
    maximum_width: int,
    maximum_height: int,
    maximum_pixels: int,
) -> None:
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(io.BytesIO(image_bytes)) as image:
                width, height = image.size
                if width > maximum_width or height > maximum_height:
                    raise ImageValidationError("Image dimension limit exceeded.")
                if width * height > maximum_pixels:
                    raise ImageValidationError("Image pixel limit exceeded.")
                if (width, height) != (declared_width, declared_height):
                    raise ImageValidationError("Declared image dimensions do not match image data.")
                image.verify()
            with Image.open(io.BytesIO(image_bytes)) as image:
                image.load()
    except ImageValidationError:
        raise
    except (Image.DecompressionBombError, Image.DecompressionBombWarning):
        raise ImageValidationError("Image pixel limit exceeded.") from None
    except (UnidentifiedImageError, OSError, SyntaxError, ValueError):
        raise ImageValidationError("Image data is invalid.") from None


class PaddleOcrEngine:
    def __init__(self):
        self._engine = None

    def initialize(self) -> None:
        if self._engine is None:
            from paddleocr import PaddleOCR

            self._engine = PaddleOCR(
                use_doc_orientation_classify=False,
                use_doc_unwarping=False,
                use_textline_orientation=False,
            )

    def recognize(self, image_bytes: bytes) -> list[tuple[str, float]]:
        self.initialize()
        import numpy

        image = numpy.asarray(Image.open(io.BytesIO(image_bytes)).convert("RGB"))
        results = self._engine.predict(image)
        blocks: list[tuple[str, float]] = []
        for result in results:
            data = result.json.get("res", {})
            for text, confidence in zip(data.get("rec_texts", []), data.get("rec_scores", [])):
                blocks.append((str(text), float(confidence)))
        return blocks


class FakeOcrEngine:
    """Explicit smoke-test engine; it never imports or downloads Paddle models."""

    def recognize(self, image_bytes: bytes) -> list[tuple[str, float]]:
        return []


def create_engine(name: str) -> OcrEngine:
    normalized = name.strip().lower()
    if normalized == "paddle":
        return PaddleOcrEngine()
    if normalized == "fake":
        return FakeOcrEngine()
    raise ValueError("OCR_ENGINE must be either 'paddle' or 'fake'.")

import base64
import io
import multiprocessing
import os
import threading
import time

from fastapi.testclient import TestClient
from PIL import Image

from app.main import create_app
from app.ocr_engine import FakeOcrEngine, create_engine


def png(width: int = 10, height: int = 10, color: str = "white") -> bytes:
    output = io.BytesIO()
    Image.new("RGB", (width, height), color=color).save(output, format="PNG")
    return output.getvalue()


class FakeEngine:
    def __init__(self, outcomes=None):
        self.outcomes = outcomes or {}

    def recognize(self, image_bytes: bytes):
        color = Image.open(io.BytesIO(image_bytes)).getpixel((0, 0))
        outcome = self.outcomes.get(color, [("ok", 1.0)])
        if isinstance(outcome, Exception):
            raise outcome
        return outcome


class HangingThenHealthyEngine:
    def recognize(self, image_bytes: bytes):
        color = Image.open(io.BytesIO(image_bytes)).getpixel((0, 0))
        if color == (255, 0, 0):
            while True:
                time.sleep(1)
        return [("recovered", 1.0)]


class CrashingThenHealthyEngine:
    def recognize(self, image_bytes: bytes):
        color = Image.open(io.BytesIO(image_bytes)).getpixel((0, 0))
        if color == (0, 0, 0):
            os._exit(23)
        return [("recovered", 1.0)]


class InitializingEngine:
    def __init__(self, marker: str, fail: bool = False):
        self.marker = marker
        self.fail = fail

    def initialize(self):
        with open(self.marker, "a", encoding="utf-8") as stream:
            stream.write(f"{os.getpid()}\n")
        if self.fail:
            raise RuntimeError("model unavailable")

    def recognize(self, image_bytes: bytes):
        return [("ready", 1.0)]


def page(number: int, content: bytes | None = None, width: int = 10, height: int = 10):
    return {
        "pageNumber": number,
        "imageBase64": base64.b64encode(content or png(width, height)).decode("ascii"),
        "width": width,
        "height": height,
    }


def test_health_has_distinct_liveness_and_readiness():
    with TestClient(create_app(FakeEngine())) as client:
        assert client.get("/health").json() == {"status": "ok"}
        assert client.get("/health/live").json() == {"status": "ok"}
        ready = client.get("/health/ready")
        assert ready.status_code == 200
        assert ready.json() == {"status": "ready"}


def test_liveness_does_not_initialize_model_and_readiness_reports_failure(tmp_path):
    marker = tmp_path / "initialized.txt"
    with TestClient(create_app(InitializingEngine(str(marker), fail=True))) as client:
        assert client.get("/health/live").status_code == 200
        assert not marker.exists()
        response = client.get("/health/ready")
    assert response.status_code == 503
    assert response.json() == {"status": "not_ready"}
    assert len(marker.read_text(encoding="utf-8").splitlines()) == 1


def test_model_is_initialized_once_for_concurrent_pages(tmp_path):
    marker = tmp_path / "initialized.txt"
    with TestClient(create_app(InitializingEngine(str(marker)))) as client:
        response = client.post("/v1/ocr/pages", json={"pages": [page(1), page(2), page(3)]})
    assert [item["status"] for item in response.json()["pages"]] == ["completed"] * 3
    assert len(marker.read_text(encoding="utf-8").splitlines()) == 1


def test_fake_engine_is_explicit_and_model_free():
    assert isinstance(create_engine("fake"), FakeOcrEngine)
    try:
        create_engine("disabled")
    except ValueError as exception:
        assert "OCR_ENGINE" in str(exception)
    else:
        raise AssertionError("unknown OCR engine must fail configuration")


def test_recognition_preserves_page_and_block_order():
    engine = FakeEngine({(255, 255, 255): [("second", 0.8), ("first", 0.9)]})
    with TestClient(create_app(engine)) as client:
        response = client.post("/v1/ocr/pages", json={"pages": [page(7)]})
    assert response.status_code == 200
    assert response.json() == {
        "pages": [{
            "pageNumber": 7,
            "status": "completed",
            "blocks": [
                {"order": 0, "text": "second", "confidence": 0.8},
                {"order": 1, "text": "first", "confidence": 0.9},
            ],
            "error": None,
        }]
    }


def test_rejects_request_body_limit():
    with TestClient(create_app(FakeEngine(), maximum_request_bytes=40)) as client:
        assert client.post("/v1/ocr/pages", content=b"{" + b"x" * 100).status_code == 413


def test_rejects_invalid_image_even_when_base64_is_valid():
    with TestClient(create_app(FakeEngine())) as client:
        response = client.post("/v1/ocr/pages", json={"pages": [page(1, b"not an image")]})
    assert response.status_code == 422
    assert response.json() == {"detail": "Image data is invalid."}


def test_rejects_actual_dimensions_that_exceed_pixel_limit():
    image = png(11, 10)
    with TestClient(create_app(FakeEngine(), maximum_image_pixels=100)) as client:
        response = client.post("/v1/ocr/pages", json={"pages": [page(1, image, 11, 10)]})
    assert response.status_code == 422
    assert response.json() == {"detail": "Image pixel limit exceeded."}


def test_rejects_pillow_decompression_bomb_warning(monkeypatch):
    monkeypatch.setattr(Image, "MAX_IMAGE_PIXELS", 100)
    image = png(11, 10)
    with TestClient(create_app(FakeEngine(), maximum_image_pixels=1_000)) as client:
        response = client.post("/v1/ocr/pages", json={"pages": [page(1, image, 11, 10)]})
    assert response.status_code == 422
    assert response.json() == {"detail": "Image pixel limit exceeded."}


def test_rejects_actual_dimension_limit_and_declared_dimension_mismatch():
    image = png(11, 10)
    with TestClient(create_app(FakeEngine(), maximum_image_width=10)) as client:
        assert client.post("/v1/ocr/pages", json={"pages": [page(1, image, 11, 10)]}).status_code == 422
    with TestClient(create_app(FakeEngine())) as client:
        response = client.post("/v1/ocr/pages", json={"pages": [page(1, image, 10, 10)]})
    assert response.status_code == 422
    assert response.json() == {"detail": "Declared image dimensions do not match image data."}


def test_timeout_terminates_worker_and_next_page_recovers_without_thread_leakage():
    before_threads = {thread.ident for thread in threading.enumerate()}
    request = {"pages": [page(1, png(color="red")), page(2, png(color="blue"))]}
    app = create_app(HangingThenHealthyEngine(), page_timeout_seconds=0.1)
    with TestClient(app) as client:
        response = client.post("/v1/ocr/pages", json=request)
        worker_pids = app.state.ocr_pool.worker_pids
        assert len(worker_pids) >= 2
        assert worker_pids[0] != worker_pids[-1]
        assert worker_pids[0] not in {child.pid for child in multiprocessing.active_children()}
    assert {thread.ident for thread in threading.enumerate()} == before_threads
    assert [item["status"] for item in response.json()["pages"]] == ["timeout", "completed"]


def test_worker_crash_isolated_and_next_page_recovers():
    app = create_app(CrashingThenHealthyEngine(), page_timeout_seconds=3)
    with TestClient(app) as client:
        response = client.post(
            "/v1/ocr/pages",
            json={"pages": [page(1, png(color="black")), page(2, png(color="blue"))]},
        )
        assert [item["status"] for item in response.json()["pages"]] == ["failed", "completed"]
        # Windows can recycle a just-exited process id immediately; two starts are the durable signal.
        assert len(app.state.ocr_pool.worker_pids) >= 2


def test_page_failure_is_isolated_and_details_are_hidden():
    engine = FakeEngine({(255, 0, 0): RuntimeError("engine details")})
    with TestClient(create_app(engine)) as client:
        response = client.post(
            "/v1/ocr/pages",
            json={"pages": [page(1, png(color="red")), page(2, png(color="blue"))]},
        )
    pages = response.json()["pages"]
    assert [item["status"] for item in pages] == ["failed", "completed"]
    assert pages[0]["error"] == "OCR page failed."
    assert "engine details" not in pages[0]["error"]

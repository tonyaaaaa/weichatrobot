import base64
import time

from fastapi.testclient import TestClient

from app.main import create_app


class FakeEngine:
    def __init__(self, outcomes):
        self.outcomes = outcomes

    def recognize(self, image_bytes: bytes):
        outcome = self.outcomes[image_bytes]
        if isinstance(outcome, Exception):
            raise outcome
        if callable(outcome):
            return outcome()
        return outcome


def page(number: int, content: bytes = b"image", width: int = 10, height: int = 10):
    return {
        "pageNumber": number,
        "imageBase64": base64.b64encode(content).decode("ascii"),
        "width": width,
        "height": height,
    }


def test_health():
    response = TestClient(create_app(FakeEngine({}))).get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_recognition_preserves_page_and_block_order():
    engine = FakeEngine({b"image": [("second", 0.8), ("first", 0.9)]})
    response = TestClient(create_app(engine)).post("/v1/ocr/pages", json={"pages": [page(7)]})
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


def test_rejects_request_body_and_pixel_limits():
    client = TestClient(create_app(FakeEngine({}), maximum_request_bytes=40, maximum_image_pixels=100))
    assert client.post("/v1/ocr/pages", content=b"{" + b"x" * 100).status_code == 413
    client = TestClient(create_app(FakeEngine({}), maximum_request_bytes=4096, maximum_image_pixels=99))
    assert client.post("/v1/ocr/pages", json={"pages": [page(1)]}).status_code == 422


def test_timeout_and_page_failure_are_isolated():
    def slow():
        time.sleep(0.2)
        return [("late", 1.0)]

    engine = FakeEngine({b"slow": slow, b"bad": RuntimeError("engine details"), b"ok": [("ok", 1.0)]})
    response = TestClient(create_app(engine, page_timeout_seconds=0.1)).post(
        "/v1/ocr/pages", json={"pages": [page(1, b"slow"), page(2, b"bad"), page(3, b"ok")]}
    )
    assert response.status_code == 200
    pages = response.json()["pages"]
    assert [item["status"] for item in pages] == ["timeout", "failed", "completed"]
    assert pages[0]["error"] == "OCR page timed out."
    assert pages[1]["error"] == "OCR page failed."
    assert "engine details" not in pages[1]["error"]

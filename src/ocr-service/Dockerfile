FROM python:3.11.13-slim-bookworm

WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgl1 libglib2.0-0 libgomp1 \
    && rm -rf /var/lib/apt/lists/*
COPY pyproject.toml .
COPY app ./app
RUN pip install --no-cache-dir ".[production]"
ENV PYTHONUNBUFFERED=1
ENV OCR_ENGINE=paddle
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health/ready', timeout=2).read()"
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]

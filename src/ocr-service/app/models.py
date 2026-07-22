import base64
import binascii

from pydantic import BaseModel, ConfigDict, Field, field_validator


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
